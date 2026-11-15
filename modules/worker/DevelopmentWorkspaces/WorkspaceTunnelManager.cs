using System.Net;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public interface IWorkspaceTunnelManager
{
    Task<int> EnsureAsync(WorkspaceRuntimeTarget target, CancellationToken ct, int remotePort = 3773, string service = "t3");
    Task RemoveAsync(string vmId, long generation, CancellationToken ct);
}

public sealed class WorkspaceTunnelManager(WorkspaceRuntimeTransport transport, IOptions<DevelopmentWorkspaceWorkerOptions> options) : IWorkspaceTunnelManager, IDisposable
{
    private readonly Dictionary<string, (SshClient Client, ForwardedPortLocal Forward, int Port)> tunnels = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<int> EnsureAsync(WorkspaceRuntimeTarget target, CancellationToken ct, int remotePort = 3773, string service = "t3")
    {
        if (remotePort is < 1024 or > 65535 || service.Length is < 1 or > 20 ||
            service.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new InvalidOperationException("A registered workspace service and unprivileged loopback port are required.");
        await gate.WaitAsync(ct);
        try
        {
            var identity = target.VmId + ":" + target.Generation + ":" + service;
            if (tunnels.TryGetValue(identity, out var existing))
            {
                if (existing.Client.IsConnected && existing.Forward.IsStarted) return existing.Port;
                existing.Client.Dispose(); tunnels.Remove(identity);
            }
            if (!IPAddress.TryParse(options.Value.RelayBindAddress, out var bind) ||
                bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("Workspace tunnels require an explicit private interface or loopback.");
            for (var port = options.Value.RelayMinimumPort; port <= options.Value.RelayMaximumPort; port++)
            {
                if (tunnels.Values.Any(x => x.Port == port)) continue;
                var client = transport.Client(target);
                try
                {
                    await client.ConnectAsync(ct);
                    var forward = new ForwardedPortLocal(bind.ToString(), (uint)port, "127.0.0.1", (uint)remotePort);
                    client.AddForwardedPort(forward);
                    forward.Start();
                    tunnels.Add(identity, (client, forward, port));
                    return port;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    client.Dispose();
                }
                catch
                {
                    client.Dispose(); throw;
                }
            }
            throw new InvalidOperationException("The reserved workspace transport port range is exhausted.");
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(string vmId, long generation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var prefix = vmId + ":" + generation + ":";
            foreach (var key in tunnels.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                if (tunnels.Remove(key, out var tunnel)) tunnel.Client.Dispose();
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var tunnel in tunnels.Values) tunnel.Client.Dispose();
        tunnels.Clear(); gate.Dispose();
    }
}
