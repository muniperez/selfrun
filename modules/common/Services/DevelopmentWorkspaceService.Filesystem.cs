using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

// Dev environment resource telemetry, on-demand folder sizes and confirmed folder deletion (#156).
public sealed partial class DevelopmentWorkspaceService
{
    public const string WorkspaceRoot = "/workspace";
    public const int FilesystemPolicyVersion = 1;
    public const int ResourceStaleSeconds = 120;
    private const long CodingUid = 2000;
    public static readonly TimeSpan DiskUsageRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan TargetSelectionLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] DeletableRoots = ["repos", "home", "artifacts"];
    private static readonly string[] ErrorCategories = ["permission_denied", "vanished", "separate_mount", "io_error",
        "limit_reached", "name_not_supported", "changed_during_scan"];
    private static readonly string[] RefusalCodes = ["invalid_path", "protected_path", "target_identity_required",
        "symbolic_link", "not_a_directory", "not_found", "permission_denied", "separate_mount", "target_changed",
        "not_owned_by_coding_user", "invalid_workspace", "unknown_mode", "refuse_root", "invalid_request",
        "vanished", "io_error", "changed_during_scan"];
    private static readonly string[] UnavailableReasons = ["unreadable", "warming_up", "counters_reset",
        "no_elapsed_time", "probe_pending", "probe_timeout", "sampling_failed"];
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static bool IsActiveFilesystemJob(string state) => state is "queued" or "running";

    // ---- Resource telemetry -------------------------------------------------------------------

    /// <summary>Validates a runtime status resource sample and returns a bounded normalized JSON document or null.</summary>
    public static string? NormalizeResourceSample(JsonElement status, string vmId, long generation, DateTimeOffset now)
    {
        if (!status.TryGetProperty("resources", out var sample) || sample.ValueKind != JsonValueKind.Object) return null;
        try
        {
            if (Int(sample, "schemaVersion") != 1 || Str(sample, "vmId") != vmId || Int(sample, "generation") != generation)
                return null;
            var observed = sample.GetProperty("observedAt").GetDouble();
            if (double.IsNaN(observed) || observed <= 0) return null;
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(observed * 1000)));
            if (observedAt > now) observedAt = now;
            if (observedAt < now.AddMinutes(-10)) return null;
            var unavailable = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (sample.TryGetProperty("unavailable", out var reasons) && reasons.ValueKind == JsonValueKind.Object)
                foreach (var reason in reasons.EnumerateObject().Take(16))
                    if (reason.Name.Length <= 64 && reason.Name.All(x => char.IsAsciiLetter(x) || x == '.')
                        && reason.Value.ValueKind == JsonValueKind.String && UnavailableReasons.Contains(reason.Value.GetString()))
                        unavailable[reason.Name] = reason.Value.GetString()!;
            DevelopmentWorkspaceCpuResources? cpu = null;
            if (sample.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                var count = Int(c, "logicalCount");
                var utilization = OptionalDouble(c, "utilizationPercent");
                var window = OptionalDouble(c, "windowSeconds");
                if (count is < 1 or > 4096 || utilization is < 0 or > 100 || window is < 0 or > 86400) return null;
                cpu = new((int)count, utilization, window);
            }
            DevelopmentWorkspaceMemoryResources? memory = null;
            if (sample.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                var total = Bytes(m, "totalBytes");
                var available = Bytes(m, "availableBytes");
                if (total <= 0 || available > total) return null;
                memory = new(total, available);
            }
            var filesystems = new List<DevelopmentWorkspaceFilesystemResources>();
            if (sample.TryGetProperty("filesystems", out var fs) && fs.ValueKind == JsonValueKind.Array)
            {
                if (fs.GetArrayLength() > 2) return null;
                foreach (var item in fs.EnumerateArray())
                {
                    var paths = item.GetProperty("paths").EnumerateArray().Select(x => x.GetString()).ToArray();
                    if (paths.Length is < 1 or > 2 || paths.Any(x => x is not ("/" or WorkspaceRoot))) return null;
                    var total = Bytes(item, "totalBytes");
                    var free = Bytes(item, "freeBytes");
                    var available = Bytes(item, "availableBytes");
                    if (free > total || available > free) return null;
                    long? inodes = item.TryGetProperty("inodesTotal", out var i) && i.ValueKind == JsonValueKind.Number ? Bytes(item, "inodesTotal") : null;
                    long? inodesFree = item.TryGetProperty("inodesAvailable", out var j) && j.ValueKind == JsonValueKind.Number ? Bytes(item, "inodesAvailable") : null;
                    int? nested = item.TryGetProperty("nestedMountsExcluded", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
                    if (inodesFree > inodes || nested is < 0 or > 10000) return null;
                    filesystems.Add(new(paths!, total, free, available, inodes, inodesFree, nested));
                }
            }
            return JsonSerializer.Serialize(new StoredResources(observedAt, cpu, memory, filesystems, unavailable), StoredJson);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    public static string NormalizeCapabilities(JsonElement status)
    {
        if (!status.TryGetProperty("capabilities", out var values) || values.ValueKind != JsonValueKind.Array) return "";
        return string.Join(',', values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!).Where(x => x is "resources-v1" or "disk-usage-v1" or "directory-delete-v1")
            .Distinct().Order(StringComparer.Ordinal));
    }

    public DevelopmentWorkspaceVmResources? Resources(DevelopmentWorkspaceVm vm)
    {
        if (vm.ResourceSampleGeneration != vm.Generation || vm.ResourceSampleReceivedAt is null) return null;
        var capabilities = Capabilities(vm);
        var running = vm.Phase == "ready" && vm.DesiredState == "running";
        var diskUsage = running && capabilities.Contains("disk-usage-v1");
        var deletion = diskUsage && capabilities.Contains("directory-delete-v1");
        if (vm.ResourceSampleJson is null || !capabilities.Contains("resources-v1"))
            return new("unsupported", null, vm.ResourceSampleReceivedAt, null, null, [], new Dictionary<string, string>(), diskUsage, deletion);
        StoredResources? stored;
        try { stored = JsonSerializer.Deserialize<StoredResources>(vm.ResourceSampleJson, StoredJson); }
        catch (JsonException) { stored = null; }
        if (stored is null)
            return new("unsupported", null, vm.ResourceSampleReceivedAt, null, null, [], new Dictionary<string, string>(), diskUsage, deletion);
        var state = !running ? "historical"
            : vm.ResourceSampleReceivedAt < Now.AddSeconds(-ResourceStaleSeconds) ? "stale" : "current";
        return new(state, stored.ObservedAt, vm.ResourceSampleReceivedAt, stored.Cpu, stored.Memory, stored.Filesystems,
            stored.Unavailable, diskUsage, deletion);
    }

    private static string[] Capabilities(DevelopmentWorkspaceVm vm) =>
        (vm.RuntimeCapabilities ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    // ---- Folder size and deletion jobs (user-facing API) ---------------------------------------

    public Task<DevelopmentWorkspaceFilesystemJob> StartDiskUsageScanAsync(string workspaceId, string organizationId, string vmId,
        DevelopmentWorkspaceDiskUsageScanRequest request, string actorId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateIdempotencyKey(request.IdempotencyKey);
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            var vm = Vm(workspace, vmId, request.Generation);
            var digest = Digest(new { request.Generation, request.TargetToken });
            if (await ReplayAsync(vm, DevelopmentWorkspaceFilesystemJobKinds.DiskUsage, request.IdempotencyKey, digest, ct) is { } replay)
                return replay;
            await RequireFilesystemReadyAsync(vm, "disk-usage-v1", ct);
            await RequireNoActiveFilesystemJobAsync(vm, ct);
            string[] path = [];
            long? device = null, inode = null;
            string? source = null;
            if (request.TargetToken is not null)
            {
                var target = await ResolveTargetAsync(workspace, vm, request.TargetToken, ct);
                if (!target.Entry.Selectable)
                    throw Invalid("workspace_folder_not_openable", "This folder cannot be opened.");
                path = target.Path; device = target.Entry.Device; inode = target.Entry.Inode; source = target.Job.Id;
            }
            return AddFilesystemJob(vm, DevelopmentWorkspaceFilesystemJobKinds.DiskUsage, path, device, inode, source,
                request.IdempotencyKey, digest, actorId, null);
        }, ct);

    public Task<DevelopmentWorkspaceFilesystemJob> StartDirectoryDeletionAsync(string workspaceId, string organizationId, string vmId,
        DevelopmentWorkspaceDirectoryDeletionRequest request, string actorId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateIdempotencyKey(request.IdempotencyKey);
            ValidateText(request.TargetToken, 256);
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            var vm = Vm(workspace, vmId, request.Generation);
            var digest = Digest(new { request.Generation, request.TargetToken, request.ConfirmationPath, request.AcknowledgePermanentLoss });
            if (await ReplayAsync(vm, DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion, request.IdempotencyKey, digest, ct) is { } replay)
                return replay;
            await RequireFilesystemReadyAsync(vm, "directory-delete-v1", ct);
            var target = await ResolveTargetAsync(workspace, vm, request.TargetToken, ct);
            if (!target.Entry.Deletable || !Deletable(target.Path))
                throw Invalid("workspace_folder_protected", "This folder is protected and cannot be deleted.");
            if (!request.AcknowledgePermanentLoss)
                throw Invalid("workspace_deletion_not_acknowledged", "Confirm that the folder and its contents will be permanently deleted.");
            if (!string.Equals(request.ConfirmationPath, DisplayPath(target.Path), StringComparison.Ordinal))
                throw Invalid("workspace_deletion_confirmation_mismatch", "Type the full folder path exactly to confirm deletion.");
            await RequireNoActiveFilesystemJobAsync(vm, ct);
            var job = AddFilesystemJob(vm, DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion, target.Path,
                target.Entry.Device, target.Entry.Inode, target.Job.Id, request.IdempotencyKey, digest, actorId, Now);
            db.AuditEvents.Add(new AuditEvent
            {
                Id = ids.NewId("audit"), OrganizationId = vm.OrganizationId, Actor = actorId, CreatedAt = Now,
                Action = "development_workspace.folder_deletion_requested",
                Details = JsonSerializer.Serialize(new
                {
                    workspaceId = vm.WorkspaceId, vmId = vm.Id, generation = vm.Generation, jobId = job.Id,
                    path = DisplayPath(target.Path), estimatedBytes = target.Entry.Bytes, confirmedAt = Now
                })
            });
            return job;
        }, ct);

    public async Task<DevelopmentWorkspaceFilesystemJob> GetFilesystemJobAsync(string workspaceId, string organizationId,
        string vmId, string jobId, CancellationToken ct)
    {
        EnsureEnabled();
        return await db.DevelopmentWorkspaceFilesystemJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId
                   && x.OrganizationId == organizationId && x.WorkspaceId == workspaceId && x.VmId == vmId, ct)
               ?? throw Invalid("workspace_filesystem_job_not_found", "Folder job was not found.");
    }

    public Task<DevelopmentWorkspaceFilesystemJob> CancelFilesystemJobAsync(string workspaceId, string organizationId,
        string vmId, string jobId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var job = await db.DevelopmentWorkspaceFilesystemJobs.SingleOrDefaultAsync(x => x.Id == jobId
                          && x.OrganizationId == organizationId && x.WorkspaceId == workspaceId && x.VmId == vmId, ct)
                      ?? throw Invalid("workspace_filesystem_job_not_found", "Folder job was not found.");
            if (job.State == "queued")
            {
                job.State = "cancelled"; job.CompletedAt = Now; job.ErrorCode = "cancelled";
                TouchJob(job);
                if (job.Kind == DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion) AuditDeletion(job, "cancelled");
            }
            else if (job.State == "running" && !job.CancelRequested)
            {
                job.CancelRequested = true; TouchJob(job);
            }
            return job;
        }, ct);

    public DevelopmentWorkspaceFilesystemJobResponse ToFilesystemJobResponse(DevelopmentWorkspaceFilesystemJob job)
    {
        var path = PathOf(job);
        DevelopmentWorkspaceDiskUsageResult? usage = null;
        DevelopmentWorkspaceDirectoryDeletionResult? deletion = null;
        if (job.ResultJson is not null && job.Kind == DevelopmentWorkspaceFilesystemJobKinds.DiskUsage)
        {
            var stored = JsonSerializer.Deserialize<StoredScan>(job.ResultJson, StoredJson)!;
            var targetsExpire = job.CompletedAt?.Add(TargetSelectionLifetime);
            var live = targetsExpire > Now;
            usage = new(stored.TotalBytes, stored.FilesHereBytes, stored.Complete, stored.LimitReached, stored.OmittedEntries,
                stored.OmittedBytes, stored.HardLinksApproximate, stored.Errors, targetsExpire,
                stored.Entries.Select(x => new DevelopmentWorkspaceDiskUsageEntry(x.Name, DisplayPath([.. path, x.Name]), x.Bytes,
                    x.State, x.Errors, x.Selectable && live, x.Deletable && live,
                    live && x.Selectable ? $"{job.Id}.{x.Token}" : null, ProtectedReason(path, x))).ToArray());
        }
        else if (job.ResultJson is not null)
        {
            var stored = JsonSerializer.Deserialize<StoredDeletion>(job.ResultJson, StoredJson)!;
            deletion = new(stored.Outcome, stored.RemovedEntries, stored.RemovedBytes, stored.LimitReached, stored.Errors);
        }
        return new(job.Id, job.VmId, job.Generation, job.Kind, job.State, DisplayPath(path), job.CreatedAt, job.StartedAt,
            job.CompletedAt, job.ExpiresAt, job.CancelRequested, job.ErrorCode, FilesystemErrorMessage(job.ErrorCode), usage, deletion);
    }

    // ---- Worker-only reconciliation ------------------------------------------------------------

    public Task<DevelopmentWorkspaceFilesystemJob?> ClaimFilesystemJobAsync(int maxRunning, CancellationToken ct) =>
        AtomicAsync<DevelopmentWorkspaceFilesystemJob?>(async () =>
        {
            if (!await maintenance.CanClaimAsync(db, ct)) return null;
            if (await db.DevelopmentWorkspaceFilesystemJobs.CountAsync(x => x.State == "running", ct) >= Math.Clamp(maxRunning, 1, 2))
                return null;
            var queued = await db.DevelopmentWorkspaceFilesystemJobs.Where(x => x.State == "queued")
                .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, false, 10, ct);
            foreach (var job in queued)
            {
                var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == job.VmId, ct);
                if (await db.DevelopmentWorkspaceFilesystemJobs.AnyAsync(x => x.VmId == job.VmId && x.State == "running", ct))
                    continue;
                if (vm.Generation != job.Generation || vm.Phase != "ready" || vm.DesiredState != "running" || vm.ErrorCode is not null)
                {
                    FinishJob(job, "failed", null, "dev_environment_not_running");
                    continue;
                }
                job.State = "running"; job.StartedAt = Now; TouchJob(job);
                return job;
            }
            return null;
        }, ct);

    public async Task<IReadOnlyList<DevelopmentWorkspaceFilesystemJob>> RunningFilesystemJobsAsync(CancellationToken ct) =>
        await db.DevelopmentWorkspaceFilesystemJobs.AsNoTracking().Where(x => x.State == "running")
            .ToDateTimeOffsetOrderedListAsync(db, x => x.StartedAt!.Value, x => x.StartedAt!.Value, false, 20, ct);

    /// <summary>Applies a runtime job report. Never replays a deletion; an unreadable outcome stays unknown.</summary>
    public Task<DevelopmentWorkspaceFilesystemJob> RecordFilesystemJobAsync(string jobId, JsonElement? report, string? failure,
        CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var job = await db.DevelopmentWorkspaceFilesystemJobs.SingleAsync(x => x.Id == jobId, ct);
            if (job.State != "running") return job;
            var delete = job.Kind == DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion;
            if (failure is not null)
            {
                FinishJob(job, delete && failure != "not_started" ? "outcome_unknown" : "failed", null, failure);
                return job;
            }
            if (report is not { } value || value.ValueKind != JsonValueKind.Object) return job;
            var state = value.TryGetProperty("state", out var s) ? s.GetString() : null;
            switch (state)
            {
                case "running":
                    return job;
                case "not_found":
                    if (job.StartedAt < Now.AddMinutes(-2)) FinishJob(job, "failed", null, "not_started");
                    return job;
                case "lost":
                    FinishJob(job, delete ? "outcome_unknown" : "failed", null, "job_result_lost");
                    return job;
                case "completed" when value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object:
                    if (result.TryGetProperty("refused", out var refused))
                    {
                        var code = refused.GetString();
                        FinishJob(job, "failed", null, RefusalCodes.Contains(code) ? code : "refused");
                    }
                    else if (delete) RecordDeletion(job, result);
                    else RecordScan(job, result);
                    return job;
                default:
                    FinishJob(job, delete ? "outcome_unknown" : "failed", null, "invalid_job_report");
                    return job;
            }
        }, ct);

    public Task<int> PruneFilesystemJobsAsync(CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var now = Now;
            var finished = db.DevelopmentWorkspaceFilesystemJobs.Where(x => x.Kind == DevelopmentWorkspaceFilesystemJobKinds.DiskUsage
                && x.State != "queued" && x.State != "running");
            // SQLite cannot compare DateTimeOffset values server-side.
            var expired = db.Database.IsSqlite()
                ? (await finished.ToListAsync(ct)).Where(x => x.ExpiresAt < now).Take(200).ToList()
                : await finished.Where(x => x.ExpiresAt < now).Take(200).ToListAsync(ct);
            db.DevelopmentWorkspaceFilesystemJobs.RemoveRange(expired);
            return expired.Count;
        }, ct);

    // ---- Helpers --------------------------------------------------------------------------------

    private void RecordScan(DevelopmentWorkspaceFilesystemJob job, JsonElement result)
    {
        try
        {
            if (Int(result, "schemaVersion") != 1 || Int(result, "policyVersion") != FilesystemPolicyVersion
                || !PathOf(job).SequenceEqual(result.GetProperty("path").EnumerateArray().Select(x => x.GetString())))
                throw new FormatException();
            var target = result.GetProperty("target");
            if (job.TargetDevice is not null && (Bytes(target, "dev") != job.TargetDevice || Bytes(target, "ino") != job.TargetInode))
            {
                FinishJob(job, "failed", null, "target_changed");
                return;
            }
            var entries = new List<StoredEntry>();
            foreach (var entry in result.GetProperty("entries").EnumerateArray().Take(500))
            {
                var name = Str(entry, "name") ?? throw new FormatException();
                var selectable = entry.GetProperty("selectable").GetBoolean() && ValidComponent(name);
                var errors = entry.GetProperty("errors").EnumerateArray().Select(x => x.GetString())
                    .Where(x => ErrorCategories.Contains(x)).Select(x => x!).Distinct().ToList();
                long? bytes = entry.GetProperty("bytes").ValueKind == JsonValueKind.Null ? null : Bytes(entry, "bytes");
                var entryState = Str(entry, "state") is "complete" or "partial" or "excluded" or "unreadable" ? Str(entry, "state")! : "unreadable";
                var deletable = selectable && entry.GetProperty("deletable").GetBoolean() && Deletable([.. PathOf(job), name]);
                entries.Add(new(name.Length > 255 ? name[..255] : name, bytes, entryState, errors, selectable, deletable,
                    Bytes(entry, "dev"), Bytes(entry, "ino"), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                    Int(entry, "uid") == CodingUid));
            }
            var errorsTotal = new Dictionary<string, int>();
            if (result.TryGetProperty("errors", out var counts) && counts.ValueKind == JsonValueKind.Object)
                foreach (var item in counts.EnumerateObject().Where(x => ErrorCategories.Contains(x.Name)))
                    errorsTotal[item.Name] = Math.Clamp(item.Value.GetInt32(), 0, 1_000_000);
            var limit = Str(result, "limitReached");
            var stored = new StoredScan(Bytes(result, "totalBytes"), Bytes(result, "filesHereBytes"),
                result.GetProperty("complete").GetBoolean(), limit is "time" or "entries" or "cancelled" ? limit : null,
                Math.Clamp(result.GetProperty("omittedEntries").GetInt32(), 0, 10_000_000), Bytes(result, "omittedBytes"),
                result.TryGetProperty("hardLinksApproximate", out var approx) && approx.ValueKind == JsonValueKind.True,
                errorsTotal, Bytes(target, "dev"), Bytes(target, "ino"), entries);
            FinishJob(job, limit == "cancelled" ? "cancelled" : stored.Complete ? "succeeded" : "partial",
                JsonSerializer.Serialize(stored, StoredJson), limit == "cancelled" ? "cancelled" : null);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException
                                          or OverflowException or ArgumentException)
        {
            FinishJob(job, "failed", null, "invalid_job_report");
        }
    }

    private void RecordDeletion(DevelopmentWorkspaceFilesystemJob job, JsonElement result)
    {
        try
        {
            var outcome = Str(result, "outcome");
            if (Int(result, "schemaVersion") != 1 || outcome is not ("succeeded" or "partially_deleted" or "failed" or "cancelled")
                || !PathOf(job).SequenceEqual(result.GetProperty("path").EnumerateArray().Select(x => x.GetString())))
                throw new FormatException();
            var errors = new Dictionary<string, int>();
            foreach (var item in result.GetProperty("errors").EnumerateObject().Where(x => ErrorCategories.Contains(x.Name)))
                errors[item.Name] = Math.Clamp(item.Value.GetInt32(), 0, 1_000_000);
            var limit = Str(result, "limitReached");
            var stored = new StoredDeletion(outcome, Bytes(result, "removedEntries"), Bytes(result, "removedBytes"),
                limit is "time" or "entries" or "cancelled" ? limit : null, errors);
            FinishJob(job, outcome, JsonSerializer.Serialize(stored, StoredJson), outcome == "failed" ? "deletion_failed" : null);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException
                                          or OverflowException or ArgumentException)
        {
            FinishJob(job, "outcome_unknown", null, "invalid_job_report");
        }
    }

    private void FinishJob(DevelopmentWorkspaceFilesystemJob job, string state, string? resultJson, string? errorCode)
    {
        job.State = state; job.ResultJson = resultJson; job.ErrorCode = errorCode; job.CompletedAt = Now;
        TouchJob(job);
        if (job.Kind == DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion) AuditDeletion(job, state);
    }

    private void AuditDeletion(DevelopmentWorkspaceFilesystemJob job, string outcome)
    {
        long? removed = null;
        if (job.ResultJson is not null)
            removed = JsonSerializer.Deserialize<StoredDeletion>(job.ResultJson, StoredJson)?.RemovedBytes;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = ids.NewId("audit"), OrganizationId = job.OrganizationId, Actor = job.ActorId, CreatedAt = Now,
            Action = "development_workspace.folder_deletion_completed",
            Details = JsonSerializer.Serialize(new
            {
                workspaceId = job.WorkspaceId, vmId = job.VmId, generation = job.Generation, jobId = job.Id,
                path = DisplayPath(PathOf(job)), confirmedAt = job.ConfirmedAt, outcome, errorCode = job.ErrorCode,
                removedBytes = removed
            })
        });
    }

    private DevelopmentWorkspaceFilesystemJob AddFilesystemJob(DevelopmentWorkspaceVm vm, string kind, string[] path,
        long? device, long? inode, string? source, string key, string digest, string actorId, DateTimeOffset? confirmedAt)
    {
        var job = new DevelopmentWorkspaceFilesystemJob
        {
            Id = ids.NewId("workspacefsjob"), OrganizationId = vm.OrganizationId, WorkspaceId = vm.WorkspaceId, VmId = vm.Id,
            Generation = vm.Generation, Kind = kind, PathJson = JsonSerializer.Serialize(path), TargetDevice = device,
            TargetInode = inode, SourceJobId = source, IdempotencyKey = key, RequestDigest = digest, ActorId = actorId,
            ConfirmedAt = confirmedAt, CreatedAt = Now, UpdatedAt = Now, ExpiresAt = Now.Add(DiskUsageRetention)
        };
        db.DevelopmentWorkspaceFilesystemJobs.Add(job);
        return job;
    }

    private async Task<DevelopmentWorkspaceFilesystemJob?> ReplayAsync(DevelopmentWorkspaceVm vm, string kind, string key,
        string digest, CancellationToken ct)
    {
        var existing = await db.DevelopmentWorkspaceFilesystemJobs.SingleOrDefaultAsync(
            x => x.VmId == vm.Id && x.Kind == kind && x.IdempotencyKey == key, ct);
        if (existing is null) return null;
        return existing.RequestDigest == digest && existing.Generation == vm.Generation ? existing
            : throw Conflict("workspace_idempotency_conflict", "Idempotency key was already used for another request.");
    }

    private async Task RequireFilesystemReadyAsync(DevelopmentWorkspaceVm vm, string capability, CancellationToken ct)
    {
        if (!(await maintenance.CheckAsync(db, requireOpen: true, ct)).IsAdmitted)
            throw Conflict("workspace_maintenance_fenced", "Maintenance is in progress. Try again later.");
        if (vm.Phase != "ready" || vm.DesiredState != "running" || vm.ErrorCode is not null)
            throw Conflict("workspace_vm_not_running", "The dev environment must be running and ready.");
        if (vm.ResourceSampleGeneration != vm.Generation || !Capabilities(vm).Contains(capability))
            throw Conflict("workspace_disk_usage_unsupported", "This dev environment image does not support folder sizes or deletion.");
    }

    private async Task RequireNoActiveFilesystemJobAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (await db.DevelopmentWorkspaceFilesystemJobs.AnyAsync(x => x.VmId == vm.Id && (x.State == "queued" || x.State == "running"), ct))
            throw Conflict("workspace_filesystem_job_active", "Another folder job is still running for this dev environment.");
    }

    private async Task<ResolvedTarget> ResolveTargetAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, string token,
        CancellationToken ct)
    {
        var separator = token.LastIndexOf('.');
        var expired = Conflict("workspace_folder_selection_expired", "This folder selection expired. Fetch folder sizes again.");
        if (separator <= 0 || token.Length > 256) throw expired;
        var jobId = token[..separator];
        var secret = token[(separator + 1)..];
        var job = await db.DevelopmentWorkspaceFilesystemJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId
            && x.OrganizationId == workspace.OrganizationId && x.WorkspaceId == workspace.Id && x.VmId == vm.Id
            && x.Kind == DevelopmentWorkspaceFilesystemJobKinds.DiskUsage, ct);
        if (job is null || job.ResultJson is null || job.Generation != vm.Generation || job.State is not ("succeeded" or "partial")
            || job.CompletedAt is null || job.CompletedAt.Value.Add(TargetSelectionLifetime) <= Now)
            throw expired;
        // Any deletion requested after this scan invalidates its selections.
        var deletions = await db.DevelopmentWorkspaceFilesystemJobs.AsNoTracking().Where(x => x.VmId == vm.Id
            && x.Kind == DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion).Select(x => x.CreatedAt).ToListAsync(ct);
        if (deletions.Any(x => x >= job.CompletedAt.Value)) throw expired;
        var stored = JsonSerializer.Deserialize<StoredScan>(job.ResultJson, StoredJson)!;
        var entry = stored.Entries.SingleOrDefault(x => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(x.Token), Encoding.ASCII.GetBytes(secret))) ?? throw expired;
        return new(job, [.. PathOf(job), entry.Name], entry);
    }

    private static void ValidateIdempotencyKey(string? key)
    {
        if (key is null || key.Length is < 8 or > 128 || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw Invalid("workspace_request_invalid", "A 8-128 character idempotency key is required.");
    }

    private static bool Deletable(string[] path) => path.Length >= 2 && DeletableRoots.Contains(path[0]) && path.All(ValidComponent);

    private static bool ValidComponent(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\0')
        && Encoding.UTF8.GetByteCount(name) <= 255 && !name.Any(char.IsSurrogate);

    private static string? ProtectedReason(string[] parent, StoredEntry entry) =>
        entry.Deletable ? null
        : entry.Errors.Contains("separate_mount") ? "separate_mount"
        : entry.Errors.Contains("name_not_supported") ? "name_not_supported"
        : entry.State == "unreadable" ? "unreadable"
        : !Deletable([.. parent, entry.Name]) ? "protected_path"
        : !entry.Owned ? "not_owned_by_coding_user"
        : "protected_path";

    private static string[] PathOf(DevelopmentWorkspaceFilesystemJob job) =>
        JsonSerializer.Deserialize<string[]>(job.PathJson) ?? [];

    public static string DisplayPath(IEnumerable<string> path)
    {
        var parts = path.ToArray();
        return parts.Length == 0 ? WorkspaceRoot : WorkspaceRoot + "/" + string.Join('/', parts);
    }

    private static string? FilesystemErrorMessage(string? code) => code switch
    {
        null => null,
        "cancelled" => "Cancelled.",
        "dev_environment_not_running" or "dev_environment_stopped" => "The dev environment stopped before the job could finish.",
        "target_changed" => "The folder changed since it was measured, so nothing was deleted. Fetch folder sizes again.",
        "protected_path" => "This folder is protected and cannot be deleted.",
        "not_owned_by_coding_user" => "This folder is not owned by the dev environment user, so it was not deleted.",
        "symbolic_link" or "separate_mount" => "Links and separately mounted folders are never opened or deleted.",
        "not_found" => "The folder no longer exists.",
        "permission_denied" => "The folder could not be read.",
        "job_result_lost" or "runtime_unreachable" or "invalid_job_report" =>
            "The dev environment did not report a result. Fetch folder sizes again to see what remains.",
        "not_started" => "The job did not start.",
        "deletion_failed" => "Nothing could be deleted.",
        _ => "The folder job did not complete.",
    };

    private void TouchJob(DevelopmentWorkspaceFilesystemJob job) { job.Revision++; job.UpdatedAt = Now; }

    private static long Int(JsonElement element, string name) => element.GetProperty(name).GetInt64();
    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Bytes(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetInt64();
        return value is >= 0 and <= 1L << 62 ? value : throw new FormatException();
    }
    private static double? OptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private sealed record ResolvedTarget(DevelopmentWorkspaceFilesystemJob Job, string[] Path, StoredEntry Entry);
    private sealed record StoredResources(DateTimeOffset ObservedAt, DevelopmentWorkspaceCpuResources? Cpu,
        DevelopmentWorkspaceMemoryResources? Memory, List<DevelopmentWorkspaceFilesystemResources> Filesystems,
        SortedDictionary<string, string> Unavailable);
    private sealed record StoredScan(long TotalBytes, long FilesHereBytes, bool Complete, string? LimitReached, int OmittedEntries,
        long OmittedBytes, bool HardLinksApproximate, Dictionary<string, int> Errors, long TargetDevice, long TargetInode,
        List<StoredEntry> Entries);
    private sealed record StoredEntry(string Name, long? Bytes, string State, List<string> Errors, bool Selectable, bool Deletable,
        long Device, long Inode, string Token, bool Owned = true);
    private sealed record StoredDeletion(string Outcome, long RemovedEntries, long RemovedBytes, string? LimitReached,
        Dictionary<string, int> Errors);
}
