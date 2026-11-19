using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.RuntimeAgent;

public sealed class RoutingApplier(AgentConfig config, IProcessRunner processRunner, IHttpsProbe? httpsProbe = null, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan FullReconciliationInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IHttpsProbe _probe = httpsProbe ?? new HttpsProbe();
    private RuntimeDesiredRoutingResponse? _installed;
    private long _installedAt;
    private RuntimeRoutingApplyResult[]? _observed;
    private RuntimeRoutingApplyResult[]? _reported;

    public void Invalidate() => _installed = null;

    public async Task<IReadOnlyList<RuntimeRoutingApplyResult>> ReconcileAsync(
        RuntimeDesiredRoutingResponse desired, Func<RuntimeRoutingApplyResultsRequest, CancellationToken, Task> report, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        // Version also changes when the API receives our results, and it need not change when a service port changes.
        if (_installed is null || _installed.AppId != desired.AppId || _installed.ComposeProject != desired.ComposeProject ||
            !_installed.Routes.SequenceEqual(desired.Routes) || _clock.GetElapsedTime(_installedAt) >= FullReconciliationInterval)
        {
            Invalidate();
            _observed = _reported = null;
            var failure = await InstallAsync(desired, cancellationToken);
            if (failure is not null)
            {
                if (desired.Routes.Count == 0)
                {
                    throw new InvalidOperationException(failure);
                }
                var failed = desired.Routes.Select(route => new RuntimeRoutingApplyResult(
                    route.BindingId, "failed", "pending", "routing_apply_failed", failure, now)).ToArray();
                await report(new(desired.Version, failed), cancellationToken);
                return failed;
            }
            _installed = desired;
            _installedAt = _clock.GetTimestamp();
        }

        var observed = _observed;
        if (observed is null || observed.Any(result => result.SslStatus != "ready"))
        {
            var previous = observed;
            observed = await Task.WhenAll(desired.Routes.Select(async (route, index) =>
            {
                if (previous?[index] is { SslStatus: "ready" } readyResult)
                {
                    return readyResult;
                }
                var ready = await _probe.IsReadyAsync(route.Hostname, cancellationToken);
                return new RuntimeRoutingApplyResult(route.BindingId, "applied", ready ? "ready" : "pending", ready ? null : "https_not_ready",
                    ready ? null : "The route is applied; a valid HTTPS certificate is not available yet.", now);
            }));
            _observed = observed;
        }

        if (_reported is null || (!ReferenceEquals(_reported, observed) &&
            !_reported.Select(result => result.SslStatus).SequenceEqual(observed.Select(result => result.SslStatus))))
        {
            await report(new(desired.Version, observed), cancellationToken);
            _reported = observed;
        }
        return observed;
    }

    private async Task<string?> InstallAsync(RuntimeDesiredRoutingResponse desired, CancellationToken cancellationToken)
    {
        try
        {
            if (desired.AppId != config.AppId || desired.ComposeProject != config.ComposeProject ||
                desired.Routes.Count > 100 || desired.Routes.Any(route => route.Hostname.Length > 253 ||
                    !Regex.IsMatch(route.Hostname, @"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z") || route.PathPrefix != "/"))
            {
                return "Invalid routing identity, hostname, or path.";
            }
            var requestPath = Path.Combine(Path.GetDirectoryName(config.StatePath)!, "routing-request.json");
            var temporary = requestPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(desired, JsonDefaults.Options), cancellationToken);
            File.Move(temporary, requestPath, overwrite: true);
            var apply = await RuntimeAgentDiagnostics.RunChildAsync(processRunner, "sudo", ["-n", "/opt/minicloud/agent/apply-minicloud-routing"], cancellationToken);
            if (apply.ExitCode != 0)
            {
                var message = apply.Stdout.Trim();
                return message.Length == 0 ? "Routing installation failed. Check the runtime routing helper and permissions."
                    : RuntimeLogRedactor.Redact(message[..Math.Min(message.Length, 500)]);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "Routing reconciliation failed. Check agent configuration and file permissions.";
        }
    }
}

public interface IHttpsProbe
{
    Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken);
}

public sealed class HttpsProbe : IHttpsProbe
{
    public async Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 443, timeout.Token);
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = hostname }, timeout.Token);
            return tls.IsAuthenticated;
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or SocketException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
