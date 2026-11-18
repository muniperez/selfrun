using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

public sealed record DevelopmentWorkspaceEnrollmentRequest(
    int SchemaVersion, string VmId, long Generation, string Kind, string OperationId,
    string ImageManifestSha256, JsonElement BootstrapReceipt);

[ApiController]
[AllowAnonymous]
[Route("api/development-workspaces/runtime/enroll")]
public sealed class DevelopmentWorkspaceEnrollmentController(
    MinicloudDbContext db, IOptions<DevelopmentWorkspaceOptions> options, TimeProvider clock,
    MaintenanceParticipant maintenance) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Enroll(DevelopmentWorkspaceEnrollmentRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!options.Value.Enabled) return NotFound();
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length is < 40 or > 256)
            return Unauthorized();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await maintenance.CanClaimAsync(db, ct)) return StatusCode(503);
        var vm = await db.DevelopmentWorkspaceVms.SingleOrDefaultAsync(x => x.Id == request.VmId, ct);
        var now = clock.GetUtcNow();
        if (vm?.EnrollmentTokenDigest is null || vm.EnrollmentTokenExpiresAt <= now || vm.EnrollmentTokenExpiresAt is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(vm.EnrollmentTokenDigest), Encoding.ASCII.GetBytes(digest)))
            return Unauthorized();
        var operation = await db.DevelopmentWorkspaceOperations.SingleOrDefaultAsync(x => x.Id == request.OperationId, ct);
        if (request.SchemaVersion != 1 || request.Generation != vm.Generation || request.Kind != vm.Kind ||
            vm.OperationId != request.OperationId || vm.DesiredState != "running" || vm.ProviderServerId is null ||
            vm.SshHostKeyGeneration != vm.Generation || operation is null || operation.VmId != vm.Id ||
            operation.Generation != vm.Generation || operation.Kind != "start" || operation.Status != "running" ||
            operation.LeaseExpiresAt <= now || operation.LeaseExpiresAt is null || vm.EnrolledAt is not null ||
            request.ImageManifestSha256 != vm.ImageManifestSha256 || !ReceiptMatches(request))
            return Conflict(new { code = "workspace_enrollment_stale", message = "Enrollment does not match the current prepared VM generation." });
        vm.EnrolledAt = now; vm.LastHeartbeatAt = now; vm.EnrollmentTokenDigest = null;
        vm.EnrollmentTokenExpiresAt = null; vm.Revision++; vm.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { code = "workspace_enrollment_consumed" }); }
        return Ok(new { accepted = true, vmId = vm.Id, generation = vm.Generation });
    }

    private static bool ReceiptMatches(DevelopmentWorkspaceEnrollmentRequest request)
    {
        var receipt = request.BootstrapReceipt;
        return receipt.ValueKind == JsonValueKind.Object &&
            receipt.TryGetProperty("type", out var type) && type.GetString() == "BootstrapReady" &&
            receipt.TryGetProperty("vmId", out var vm) && vm.GetString() == request.VmId &&
            receipt.TryGetProperty("generation", out var generation) && generation.TryGetInt64(out var number) && number == request.Generation &&
            receipt.TryGetProperty("kind", out var kind) && kind.GetString() == request.Kind &&
            receipt.TryGetProperty("operationId", out var operation) && operation.GetString() == request.OperationId;
    }
}
