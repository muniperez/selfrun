using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    private async Task<DevelopmentWorkspaceGitRecoverySource?> SavedGitSourceAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (vm.Kind != DevelopmentWorkspaceVmKinds.Ephemeral || vm.Generation == 0) return null;
        static void Require(bool valid)
        {
            if (!valid)
                throw Conflict("workspace_git_recovery_invalid",
                    "Previous Git-only generation has no matching verified saved source. Recovery cannot reset to the original base.");
        }

        var stop = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == vm.OperationId && x.WorkspaceId == vm.WorkspaceId && x.VmId == vm.Id
            && x.Generation == vm.Generation && x.Kind == "stop" && x.Status == "completed", ct);
        Require(stop is not null && vm.QuiescedGeneration == vm.Generation
            && vm.QuiescedOperationId == stop.Id && vm.QuiescedAt is not null
            && vm.QuiescenceReceipt is { Length: > 0 and <= 65536 }
            && vm.IgnoredDataAcknowledgedAt is not null);
        try
        {
            using var document = JsonDocument.Parse(vm.QuiescenceReceipt!);
            var receipt = document.RootElement;
            var receiptId = receipt.GetProperty("receiptId").GetString();
            Require(receipt.GetProperty("schemaVersion").GetInt32() == 1
                && receipt.GetProperty("vmId").GetString() == vm.Id
                && receipt.GetProperty("generation").GetInt64() == vm.Generation
                && receipt.GetProperty("kind").GetString() == "ephemeral"
                && receipt.GetProperty("operationId").GetString() == stop!.Id && Sha(receiptId, 64));
            using var evidence = JsonDocument.Parse(stop!.ProviderEvidenceJson ?? "{}");
            Require(evidence.RootElement.GetProperty("receiptId").GetString() == receiptId
                && evidence.RootElement.GetProperty("deleteCommitted").GetBoolean()
                && evidence.RootElement.GetProperty("repositoryCredentialRevoked").GetBoolean());
            if (receipt.GetProperty("type").GetString() == "DiscardAuthorized")
            {
                Require(vm.DiscardGeneration == vm.Generation && vm.DiscardOperationId == stop.Id
                    && vm.DiscardRequestedAt is not null && !string.IsNullOrWhiteSpace(vm.DiscardRequestedBy));
                return null;
            }

            Require(receipt.GetProperty("type").GetString() == "GitSaved" && vm.GitStatus == "saved"
                && vm.GitSavedGeneration == vm.Generation && vm.GitSavedOperationId == stop.Id
                && vm.GitSavedAt is not null && vm.GitSavedAt >= vm.QuiescedAt
                && vm.GitSaveReceipt == vm.QuiescenceReceipt && vm.PushAuthorized
                && Sha(vm.FinalHead, 40) && vm.FinalHead == vm.VerifiedRemoteCommit
                && vm.WorkBranch == $"minicloud/{vm.Id}/g{vm.Generation}"
                && vm.VerifiedRemoteRef == "refs/heads/" + vm.WorkBranch
                && vm.VerifiedRemoteRef != vm.BaseRef);
            var repositories = receipt.GetProperty("repositories");
            Require(repositories.ValueKind == JsonValueKind.Array && repositories.GetArrayLength() == 1);
            var repository = repositories[0];
            Require(repository.GetProperty("path").GetString() == "/workspace/repos/project"
                && repository.GetProperty("origin").GetString() == vm.Repository
                && repository.GetProperty("baseSha").GetString() == vm.BaseCommit
                && repository.GetProperty("branch").GetString() == vm.WorkBranch
                && repository.GetProperty("headSha").GetString() == vm.FinalHead
                && repository.GetProperty("remoteSha").GetString() == vm.FinalHead
                && repository.GetProperty("remoteRef").GetString() == vm.VerifiedRemoteRef);
            return new(vm.Repository!, vm.FinalHead!, vm.VerifiedRemoteRef!, vm.Generation, stop.Id, receiptId!);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Require(false);
            throw;
        }
    }
}
