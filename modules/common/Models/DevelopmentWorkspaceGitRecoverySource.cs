namespace Minicloud.Common.Models;

public sealed record DevelopmentWorkspaceGitRecoverySource(
    string Repository, string Commit, string RemoteRef, long Generation, string OperationId, string ReceiptId);
