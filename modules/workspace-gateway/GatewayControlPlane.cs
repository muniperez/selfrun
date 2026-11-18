using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.WorkspaceGateway;

public interface IGatewayControlPlane
{
    Task<bool> AuthorizeCertificateAsync(string domain, CancellationToken ct);
    Task<bool> ReportActivityAsync(string host, string session, CancellationToken ct);
    Task<DevelopmentWorkspaceGatewayRedemption?> RedeemAsync(string host, string code, CancellationToken ct);
    Task<DevelopmentWorkspaceGatewayTarget?> ResolveAsync(string host, string session, CancellationToken ct);
}

public sealed class GatewayControlPlane : IGatewayControlPlane, IDisposable
{
    private readonly DevelopmentWorkspaceGatewayOptions options;
    private readonly HttpClient client;

    public GatewayControlPlane(IOptions<DevelopmentWorkspaceGatewayOptions> configuration)
    {
        options = configuration.Value;
        var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            MaxConnectionsPerServer = 32, ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16
        };
        if (Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out var origin) && origin.Scheme == "http")
            transport.ConnectCallback = (context, ct) => PrivateGatewayTransport.ConnectAsync(context, origin.Host, ct);
        client = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16384 };
    }

    public Task<DevelopmentWorkspaceGatewayRedemption?> RedeemAsync(string host, string code, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceGatewayRedemption>("redeem", host, code, ct);

    public Task<DevelopmentWorkspaceGatewayTarget?> ResolveAsync(string host, string session, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceGatewayTarget>("resolve", host, session, ct);

    public async Task<bool> AuthorizeCertificateAsync(string domain, CancellationToken ct)
    {
        if (!options.TryParseHost(domain, out _, out _) || !TryControlPlaneOrigin(out var origin)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(origin, "/internal/workspace-gateway/certificate-authorization?domain=" + Uri.EscapeDataString(domain)));
        request.Headers.Add("X-Minicloud-Gateway-Credential", options.ServiceCredential);
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode == System.Net.HttpStatusCode.OK;
    }

    public async Task<bool> ReportActivityAsync(string host, string session, CancellationToken ct)
    {
        if (!options.AcceptHost(host) || session.Length != 64 || !TryControlPlaneOrigin(out var origin)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/internal/workspace-gateway/activity"));
        request.Headers.Add("X-Minicloud-Gateway-Credential", options.ServiceCredential);
        request.Content = JsonContent.Create(new DevelopmentWorkspaceGatewayReceiptRequest(host, session));
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    private async Task<T?> SendAsync<T>(string operation, string host, string receipt, CancellationToken ct)
    {
        if (!TryControlPlaneOrigin(out var origin)) return default;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/internal/workspace-gateway/" + operation));
        request.Headers.Add("X-Minicloud-Gateway-Credential", options.ServiceCredential);
        request.Content = JsonContent.Create(new DevelopmentWorkspaceGatewayReceiptRequest(host, receipt));
        using var response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(ct) : default;
    }

    private bool TryControlPlaneOrigin(out Uri origin)
    {
        origin = null!;
        return options.IsConfigured && Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out origin!) &&
            origin.UserInfo.Length == 0 && (origin.Scheme == "https" ||
            origin.Scheme == "http" && DevelopmentWorkspaceGatewayOptions.IsRelayHost(origin.Host));
    }

    public void Dispose() => client.Dispose();
}
