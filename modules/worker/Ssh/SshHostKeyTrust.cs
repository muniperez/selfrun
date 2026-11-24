using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;

namespace Minicloud.Worker.Ssh;

/// <summary>Supplies host-key pins recorded for worker-provisioned servers.</summary>
public interface ISshHostKeyPinSource
{
    /// <summary>Validates the selected registration and returns its enrolled fingerprint, if any.</summary>
    Task<string?> GetPinnedFingerprintAsync(SshTarget target, CancellationToken cancellationToken);
}

public sealed class ServerHostKeyPinSource(IServiceScopeFactory scopeFactory) : ISshHostKeyPinSource
{
    public async Task<string?> GetPinnedFingerprintAsync(SshTarget target, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var record = await db.ServerInstances.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == target.ServerInstanceId, cancellationToken);
        if (record is null || string.IsNullOrWhiteSpace(target.Host) ||
            record.IpAddress != target.Host || record.Status != "active" ||
            await db.ServerInstances.AsNoTracking().AnyAsync(s =>
                s.Id != record.Id && s.Status == "active" && s.IpAddress == target.Host, cancellationToken))
        {
            throw new SshHostKeyTrustException(SshHostKeyTrustException.UntrustedFailureCode,
                $"Selected server {target.ServerInstanceId} is missing, inactive, no longer holds {target.Host}, or shares it with another active server; refusing SSH.");
        }

        if (target.ExpectedFingerprint is not null && target.ExpectedFingerprint != record.HostKeyFingerprint)
        {
            throw new SshHostKeyTrustException(SshHostKeyTrustException.UntrustedFailureCode,
                $"Selected server {target.ServerInstanceId}'s enrolled SSH host key changed during the operation; refusing SSH.");
        }
        return record.HostKeyFingerprint;
    }
}

public static class SshHostPins
{
    public static IReadOnlySet<string> Read(string path, string host)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return File.ReadLines(path)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 2 && fields[0].Equals(host, StringComparison.OrdinalIgnoreCase) &&
                fields[1].StartsWith("SHA256:", StringComparison.Ordinal) && fields[1].Length > "SHA256:".Length)
            .Select(fields => fields[1])
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>An SSH connection was refused because the server's host key is not trusted.</summary>
public sealed class SshHostKeyTrustException(string failureCode, string message) : InvalidOperationException(message)
{
    public const string UntrustedFailureCode = "ssh_host_key_untrusted";
    public const string MismatchFailureCode = "ssh_host_key_mismatch";

    public string FailureCode { get; } = failureCode;
}
