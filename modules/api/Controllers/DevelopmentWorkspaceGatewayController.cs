using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController]
public sealed class DevelopmentWorkspaceGatewayController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("internal/workspace-gateway/certificate-authorization")]
    public async Task<IResult> AuthorizeCertificate([FromQuery] string? domain,
        [FromServices] DevelopmentWorkspaceGatewayService gateway,
        [FromServices] IOptions<DevelopmentWorkspaceGatewayOptions> options, CancellationToken ct)
    {
        NoStore();
        if (domain is null || !ServiceAuthorized(options.Value)) return Results.NotFound();
        return await gateway.AuthorizeCertificateAsync(domain, ct) ? Results.Ok() : Results.NotFound();
    }

    [Authorize]
    [HttpPost("api/development-workspaces/{workspaceId}/vms/{vmId}/launch")]
    public async Task<IResult> Launch(string workspaceId, string vmId, DevelopmentWorkspaceGatewayLaunchRequest request,
        [FromServices] DevelopmentWorkspaceGatewayService gateway,
        [FromServices] IOptions<DevelopmentWorkspaceGatewayOptions> options, CancellationToken ct)
    {
        NoStore();
        if (!options.Value.IsConfigured) return Results.StatusCode(503);
        var result = await gateway.LaunchAsync(EndpointResults.RequirePrincipal(HttpContext), workspaceId, vmId, request, ct);
        return result is null ? Results.Conflict(new { error = "workspace_gateway_not_ready_or_authorized" }) : Results.Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("internal/workspace-gateway/redeem")]
    public async Task<IResult> Redeem(DevelopmentWorkspaceGatewayReceiptRequest request,
        [FromServices] DevelopmentWorkspaceGatewayService gateway,
        [FromServices] IOptions<DevelopmentWorkspaceGatewayOptions> options, CancellationToken ct)
    {
        NoStore();
        if (!ServiceAuthorized(options.Value)) return Results.Unauthorized();
        var result = await gateway.RedeemAsync(request, ct);
        return result is null ? Results.Unauthorized() : Results.Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("internal/workspace-gateway/resolve")]
    public async Task<IResult> Resolve(DevelopmentWorkspaceGatewayReceiptRequest request,
        [FromServices] DevelopmentWorkspaceGatewayService gateway,
        [FromServices] IOptions<DevelopmentWorkspaceGatewayOptions> options, CancellationToken ct)
    {
        NoStore();
        if (!ServiceAuthorized(options.Value)) return Results.Unauthorized();
        var result = await gateway.ResolveAsync(request, ct);
        return result is null ? Results.Unauthorized() : Results.Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("internal/workspace-gateway/activity")]
    public async Task<IResult> Activity(DevelopmentWorkspaceGatewayReceiptRequest request,
        [FromServices] DevelopmentWorkspaceGatewayService gateway,
        [FromServices] DevelopmentWorkspaceService workspaces,
        [FromServices] IOptions<DevelopmentWorkspaceGatewayOptions> options, CancellationToken ct)
    {
        NoStore();
        if (!ServiceAuthorized(options.Value)) return Results.Unauthorized();
        var binding = await gateway.AuthorizeActivityAsync(request, ct);
        if (binding is null) return Results.Unauthorized();
        try
        {
            await workspaces.ActivityAsync(binding.Value.WorkspaceId, binding.Value.OrganizationId,
                binding.Value.VmId, binding.Value.Generation, ct);
            return Results.NoContent();
        }
        catch (DevelopmentWorkspaceException) { return Results.Unauthorized(); }
    }

    private bool ServiceAuthorized(DevelopmentWorkspaceGatewayOptions options)
    {
        var supplied = Request.Headers["X-Minicloud-Gateway-Credential"].ToString();
        return options.IsConfigured && supplied.Length <= 512 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(options.ServiceCredential)));
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
