using Minicloud.Common.Services;

namespace WorkspaceReconciliation;

public static class OpenBaoEndpoint
{
    private const string LegacyOrigin = "http://openbao:8200";

    public static HttpMessageHandler CreateHandler(OpenBaoSecretStoreOptions options, bool allowLegacyHttp)
    {
        if (allowLegacyHttp && options.Address == LegacyOrigin)
            return new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };

        if (!Uri.TryCreate(options.Address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new RecoveryRefused($"OpenBao requires an HTTPS origin. Only the exact private-network origin {LegacyOrigin} may opt into HTTP.");

        var handler = OpenBaoHttpHandler.Create(options);
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        return handler;
    }
}
