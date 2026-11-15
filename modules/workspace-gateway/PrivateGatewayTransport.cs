using System.Net;
using System.Net.Sockets;
using Minicloud.Common.Services;

namespace Minicloud.WorkspaceGateway;

internal static class PrivateGatewayTransport
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
        string allowedHost, CancellationToken ct)
    {
        if (!context.DnsEndPoint.Host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Unregistered gateway transport host.");
        var addresses = IPAddress.TryParse(allowedHost, out var address)
            ? [address] : await Dns.GetHostAddressesAsync(allowedHost, ct);
        if (addresses.Length == 0 || addresses.Any(ip => !DevelopmentWorkspaceGatewayOptions.IsPrivateAddress(ip.ToString())))
            throw new HttpRequestException("Gateway transport must resolve exclusively to private addresses.");
        foreach (var ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                if (ct.IsCancellationRequested || ip.Equals(addresses[^1])) throw;
            }
        }
        throw new HttpRequestException("Gateway transport connection failed.");
    }
}
