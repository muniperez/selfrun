using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;

namespace Minicloud.Worker.Ssh;

/// <summary>Supplies host-key pins recorded for worker-provisioned servers.</summary>
public interface ISshHostKeyPinSource
{
    /// <summary>Returns the verified <c>SHA256:</c> fingerprint for the server currently registered at <paramref name="host"/>.</summary>
    Task<string?> GetPinnedFingerprintAsync(string host, CancellationToken cancellationToken);
}

public sealed class ServerHostKeyPinSource(IServiceScopeFactory scopeFactory) : ISshHostKeyPinSource
{
    public async Task<string?> GetPinnedFingerprintAsync(string host, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var servers = await db.ServerInstances.AsNoTracking()
            .Where(s => s.IpAddress == host)
            .Select(s => new { s.HostKeyFingerprint, s.CreatedAt })
            .ToListAsync(cancellationToken);
        // Only the newest registration for a reused address counts; an older server's pin never carries over.
        return servers.OrderByDescending(s => s.CreatedAt).FirstOrDefault()?.HostKeyFingerprint;
    }
}

/// <summary>An SSH connection was refused because the server's host key is not trusted.</summary>
public sealed class SshHostKeyTrustException(string failureCode, string message) : InvalidOperationException(message)
{
    public const string UntrustedFailureCode = "ssh_host_key_untrusted";
    public const string MismatchFailureCode = "ssh_host_key_mismatch";

    public string FailureCode { get; } = failureCode;
}
