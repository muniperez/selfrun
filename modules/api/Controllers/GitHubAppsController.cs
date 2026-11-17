using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/github-apps")]
public sealed class GitHubAppsController(GitHubAppService apps, ControlPlaneAuthorization authorization,
    AuditEventService audit) : ControllerBase
{
    [HttpGet]
    public Task<IResult> Status(string organizationId, CancellationToken ct) => RunAsync(async () =>
        await CanAsync(organizationId, false, ct) ? Results.Ok(await apps.StatusAsync(organizationId, ct)) : EndpointResults.Forbidden());

    [HttpGet("setup-url")]
    public Task<IResult> SetupUrl(string organizationId, CancellationToken ct) => RunAsync(async () =>
        await CanAsync(organizationId, false, ct) ? Results.Ok(new { url = apps.SetupUrl(organizationId) }) : EndpointResults.Forbidden());

    [HttpPost("setup")]
    public Task<IResult> Begin(GitHubAppSetupRequest request, CancellationToken ct) => RunAsync(async () =>
    {
        if (!await CanAsync(request.OrganizationId, true, ct) || UserId() is null) return EndpointResults.Forbidden();
        var result = await apps.BeginAsync(request, UserId()!, ct);
        await AuditAsync(request.OrganizationId, "github.setup_started", ct);
        return Results.Ok(result);
    });

    [HttpPost("complete")]
    public Task<IResult> Complete(GitHubAppCompleteRequest request, CancellationToken ct) =>
        WithConnectionAsync(request.ConnectionId, true, async () =>
        {
            if (UserId() is null) return EndpointResults.Forbidden();
            return Results.Ok(await apps.CompleteAsync(request, UserId()!, ct));
        }, ct);

    [HttpPost("{id}/installation-url")]
    public Task<IResult> InstallationUrl(string id, CancellationToken ct) =>
        WithConnectionAsync(id, true, async () => UserId() is null ? EndpointResults.Forbidden() :
            Results.Ok(await apps.InstallationUrlAsync(id, UserId()!, ct)), ct);

    [HttpPost("installation")]
    public Task<IResult> Installation(GitHubAppInstallRequest request, CancellationToken ct) =>
        WithConnectionAsync(request.ConnectionId, true, async () => UserId() is null ? EndpointResults.Forbidden() :
            Results.Ok(await apps.AssociateAsync(request, UserId()!, ct)), ct);

    [HttpGet("{id}/repositories")]
    public Task<IResult> Repositories(string id, CancellationToken ct) =>
        WithConnectionAsync(id, false, async () => Results.Ok(await apps.RepositoriesAsync(id, ct)), ct);

    [HttpPost("{id}/reconcile")]
    public Task<IResult> Reconcile(string id, CancellationToken ct) =>
        WithConnectionAsync(id, true, async () => Results.Ok(await apps.ReconcileAsync(id, ct)), ct);

    [HttpPost("{id}/disconnect")]
    public Task<IResult> Disconnect(string id, CancellationToken ct) =>
        WithConnectionAsync(id, true, async () => Results.Ok(await apps.DisconnectAsync(id, false, ct)), ct);

    [HttpPost("{id}/reconnect")]
    public Task<IResult> Reconnect(string id, CancellationToken ct) =>
        WithConnectionAsync(id, true, async () => Results.Ok(await apps.DisconnectAsync(id, true, ct)), ct);

    [HttpDelete("{id}")]
    public Task<IResult> Remove(string id, CancellationToken ct) =>
        WithConnectionAsync(id, true, async () => { await apps.RemoveAsync(id, ct); return Results.NoContent(); }, ct);

    [AllowAnonymous, HttpPost("{id}/webhook"), RequestSizeLimit(1024 * 1024)]
    public Task<IResult> Webhook(string id, CancellationToken ct) => RunAsync(async () =>
    {
        if (Request.ContentLength > 1024 * 1024) return Results.StatusCode(413);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 1024 * 1024) return Results.StatusCode(413);
            buffer.Write(chunk, 0, count);
        }
        await apps.WebhookAsync(id, Request.Headers["X-Hub-Signature-256"].ToString(),
            Request.Headers["X-GitHub-Delivery"].ToString(), Request.Headers["X-GitHub-Event"].ToString(), buffer.ToArray(), ct);
        return Results.NoContent();
    });

    private Task<IResult> WithConnectionAsync(string id, bool manage, Func<Task<IResult>> action, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var connection = await apps.GetAsync(id, ct);
            if (!await CanAsync(connection.OrganizationId, manage, ct)) return EndpointResults.Forbidden();
            var result = await action();
            if (manage) await AuditAsync(connection.OrganizationId, "github.connection_changed", ct);
            return result;
        });

    private Task<bool> CanAsync(string org, bool manage, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), org,
            manage ? OrganizationAction.ManageGitHub : OrganizationAction.ReadGitHub, ct);

    private string? UserId()
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        return principal.IsDashboardUser ? principal.UserId : null;
    }

    private Task AuditAsync(string organizationId, string action, CancellationToken ct) =>
        audit.AppendAsync(organizationId, null, AuditEventService.ActorFor(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity()), action, null, ct);

    private async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        try { return await action(); }
        catch (GitHubAppException ex) { return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode); }
        catch (SecretStoreUnavailableException) { return EndpointResults.Problem("github_secrets_unavailable", "GitHub credentials are temporarily unavailable in OpenBao.", 503); }
        catch (DbUpdateException) { return EndpointResults.Problem("github_connection_changed", "Connection changed concurrently. Refresh status before retrying.", 409); }
        catch (Exception ex) when (ex is JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            return EndpointResults.Problem("github_response_invalid", "GitHub credentials or response could not be verified.", 502);
        }
    }
}
