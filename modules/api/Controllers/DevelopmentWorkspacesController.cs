using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/development-workspaces")]
public sealed class DevelopmentWorkspacesController(
    MinicloudDbContext db,
    ControlPlaneAuthorization authorization,
    DevelopmentWorkspaceService workspaces) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List([FromQuery] string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        var items = await workspaces.ListAsync(organizationId, ct);
        return Results.Ok(items.Select(workspaces.ToResponse));
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateDevelopmentWorkspaceRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try
        {
            var actor = EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var workspace = await workspaces.CreateAsync(request, actor.UserId ?? $"api_key:{actor.ApiKeyId}", ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/development-workspaces/{workspace.Id}", workspaces.ToResponse(workspace));
        }
        catch (DevelopmentWorkspaceException ex) { return Error(ex); }
        catch (DbUpdateException) { return EndpointResults.Conflict("workspace_name_or_idempotency_conflict", "Workspace name or idempotency key is already in use."); }
    }

    [HttpGet("{id}")]
    public async Task<IResult> Get([FromRoute] string id, [FromQuery] string organizationId, CancellationToken ct)
    {
        var workspace = await workspaces.GetAsync(id, organizationId, ct);
        if (workspace is null) return EndpointResults.NotFound("workspace_not_found", "Workspace was not found.");
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        return Results.Ok(workspaces.ToResponse(workspace));
    }

    [HttpPost("{id}/start")]
    public Task<IResult> Start([FromRoute] string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(id, organizationId, OrganizationAction.WriteApps, workspaces.StartAsync, ct);

    [HttpPost("{id}/stop")]
    public Task<IResult> Stop([FromRoute] string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(id, organizationId, OrganizationAction.WriteApps, workspaces.StopAsync, ct);

    [HttpPost("{id}/cancel-idle-stop")]
    public Task<IResult> CancelIdleStop([FromRoute] string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(id, organizationId, OrganizationAction.WriteApps, workspaces.CancelIdleStopAsync, ct);

    [HttpPut("{id}/tasks/{taskId}/lease")]
    public async Task<IResult> RenewTaskLease([FromRoute] string id, [FromRoute] string taskId,
        [FromQuery] string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try
        {
            var lease = await workspaces.RenewTaskLeaseAsync(id, organizationId, taskId, ct);
            return Results.Ok(new { lease.TaskId, lease.Generation, lease.Status, lease.ExpiresAt });
        }
        catch (DevelopmentWorkspaceException ex) { return Error(ex); }
    }

    [HttpPost("{id}/tasks/{taskId}/complete")]
    public async Task<IResult> CompleteTask([FromRoute] string id, [FromRoute] string taskId,
        [FromQuery] string organizationId, [FromBody] CompleteWorkspaceTaskRequest request, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try
        {
            var lease = await workspaces.CompleteTaskAsync(id, organizationId, taskId, request.Cancelled, ct);
            return Results.Ok(new { lease.TaskId, lease.Generation, lease.Status, lease.UpdatedAt });
        }
        catch (DevelopmentWorkspaceException ex) { return Error(ex); }
    }

    private async Task<IResult> MutateAsync(string id, string organizationId, OrganizationAction action,
        Func<string, string, CancellationToken, Task<Minicloud.Common.Models.DevelopmentWorkspace>> mutate, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, action, ct)) return EndpointResults.Forbidden();
        try
        {
            var workspace = await mutate(id, organizationId, ct);
            return Results.Ok(workspaces.ToResponse(workspace));
        }
        catch (DevelopmentWorkspaceException ex) { return Error(ex); }
    }

    private Task<bool> CanAsync(string organizationId, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, action, ct);

    private static IResult Error(DevelopmentWorkspaceException error) =>
        error.Conflict
            ? EndpointResults.Conflict(error.Code, error.Message)
            : Results.BadRequest(new ErrorResponse(new ErrorBody(error.Code, error.Message, [])));
}

public sealed record CompleteWorkspaceTaskRequest(bool Cancelled);
