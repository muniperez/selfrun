namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceWorkerOptions
{
    public const string SectionName = "DevelopmentWorkspaceWorker";
    public string ApiBaseUrl { get; set; } = "";
    public string[] ManagementAddresses { get; set; } = [];
    public string RelayBindAddress { get; set; } = "127.0.0.1";
    public int RelayMinimumPort { get; set; } = 30000;
    public int RelayMaximumPort { get; set; } = 39999;
    public int PollSeconds { get; set; } = 5;
    public int IdleShutdownDelaySeconds { get; set; } = 60;
    public int GatewayDrainSeconds { get; set; } = 75;
    public int SessionLifetimeSeconds { get; set; } = 1800;
    public string? BackupRecipient { get; set; }
    public string? BackupRecoveryId { get; set; }
    public string? BackupDestinationIdentity { get; set; }
    public int BackupRetentionDays { get; set; } = 7;
    public bool ObservedTerminalInputs { get; set; }
    public Dictionary<string, DevelopmentWorkspacePreviewService> PreviewServices { get; set; } = new()
    {
        ["connect"] = new() { Port = 774 }
    };
}

public sealed class DevelopmentWorkspacePreviewService
{
    public int Port { get; set; }
    public string? SupervisedUnit { get; set; }
}
