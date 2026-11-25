// SPDX-License-Identifier: MPL-2.0

namespace Selfrun.Cli.Config;

public sealed record SelfrunConfig(
    string App,
    string? Database,
    string? CommitSha,
    IReadOnlyDictionary<string, SelfrunServiceConfig> Services)
{
    public string? AppId { get; init; }
    public string? Plan { get; init; }
    public int? BuildJobs { get; init; }
    public IReadOnlyDictionary<string, SelfrunAssetConfig> Assets { get; init; } = new Dictionary<string, SelfrunAssetConfig>();
}

public sealed record SelfrunServiceConfig(
    string? SourcePath,
    string? Dockerfile,
    string? Image,
    int? Port,
    bool? Public,
    string? Path,
    string? HealthPath,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyDictionary<string, string>? SecretEnv = null,
    IReadOnlyDictionary<string, string>? BuildEnv = null,
    IReadOnlyDictionary<string, Selfrun.Common.Contracts.AssetMountRequest>? AssetMounts = null,
    string BuildMode = Selfrun.Common.Contracts.ServiceBuildModes.Local,
    string? BuildPlatform = null,
    IReadOnlyDictionary<string, Selfrun.Common.Contracts.StorageMountRequest>? Storage = null);

public sealed record ConfigDiagnostic(string Field, string Message);

public sealed record ConfigLoadResult(SelfrunConfig? Config, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0 && Config is not null;
}

public sealed record SelfrunAssetConfig(string SourcePath, string Dockerfile, string OutputPath);
