using Minicloud.Common.Services;
using Minicloud.WorkspaceGateway;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Minicloud.Common.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOperatorDiagnostics(builder.Configuration, "minicloud-workspace-gateway", webHost: true);
// Launch codes and websocket tickets are query parameters; never enable request URL logging.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
    server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.Configure<DevelopmentWorkspaceGatewayOptions>(
    builder.Configuration.GetSection(DevelopmentWorkspaceGatewayOptions.SectionName));
builder.Services.PostConfigure<DevelopmentWorkspaceGatewayOptions>(options => options.LoadServiceCredential());
builder.Services.AddSingleton<IGatewayControlPlane, GatewayControlPlane>();
builder.Services.AddSingleton<WorkspaceGatewayRelay>();
var trustedProxy = builder.Configuration[$"{DevelopmentWorkspaceGatewayOptions.SectionName}:TrustedProxyAddress"];
if (!string.IsNullOrEmpty(trustedProxy))
{
    if (!DevelopmentWorkspaceGatewayOptions.IsPrivateAddress(trustedProxy))
        throw new InvalidOperationException("Gateway trusted proxy must be an explicit private IP address.");
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Parse(trustedProxy));
    });
}
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/internal/certificate")
        await app.Services.GetRequiredService<WorkspaceGatewayRelay>().HandleCertificateAsync(context);
    else await next(context);
});
if (!string.IsNullOrEmpty(trustedProxy)) app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Run(context => app.Services.GetRequiredService<WorkspaceGatewayRelay>().HandleAsync(context));
app.Run();
