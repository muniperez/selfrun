using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/coding-workspaces")]
public sealed class CodingWorkspacesController(
    MinicloudDbContext db,
    CodingWorkspaceService workspaces,
    ControlPlaneAuthorization authorization) : ControllerBase
{
    [HttpPost]
    public async Task<IResult> Create(CreateCodingWorkspaceRequest request, CancellationToken ct)
    {
        if (!await CanAccessAsync(request.OrganizationId, OrganizationAction.WriteApps, ct))
            return EndpointResults.Forbidden();
        try
        {
            var created = await workspaces.CreateAsync(request, ct);
            return Results.Accepted($"/api/coding-workspaces/operations/{created.Operation.Id}",
                new CreateCodingWorkspaceResponse(CodingWorkspaceService.ToResponse(created.Workspace),
                    CodingWorkspaceService.ToResponse(created.Operation), created.Resumed));
        }
        catch (CodingWorkspaceException ex) { return ValidationError(ex.Code, ex.Message); }
        catch (ProviderCatalogException ex) { return ValidationError(ex.Code, ex.Message); }
    }

    [HttpGet]
    public async Task<IResult> List([FromQuery] string organizationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organizationId) ||
            !await CanAccessAsync(organizationId, OrganizationAction.ReadApps, ct))
            return EndpointResults.Forbidden();
        return Results.Ok((await workspaces.ListAsync(organizationId, ct)).Select(CodingWorkspaceService.ToResponse));
    }

    [HttpGet("{workspaceId}")]
    public async Task<IResult> Get(string workspaceId, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workspaceId, ct);
        if (workspace is null) return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        if (!await CanAccessAsync(workspace.OrganizationId, OrganizationAction.ReadApps, ct))
            return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        return Results.Ok(CodingWorkspaceService.ToResponse(workspace));
    }

    [HttpGet("operations/{operationId}")]
    public async Task<IResult> GetOperation(string operationId, CancellationToken ct)
    {
        var operation = await db.CodingWorkspaceOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (operation is null) return EndpointResults.NotFound("workspace_operation_not_found", "Workspace operation was not found.");
        if (!await CanAccessAsync(operation.OrganizationId, OrganizationAction.ReadApps, ct))
            return EndpointResults.NotFound("workspace_operation_not_found", "Workspace operation was not found.");
        return Results.Ok(CodingWorkspaceService.ToResponse(operation));
    }

    [HttpDelete("{workspaceId}")]
    public async Task<IResult> Destroy(string workspaceId, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workspaceId, ct);
        if (workspace is null) return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        if (!await CanAccessAsync(workspace.OrganizationId, OrganizationAction.WriteApps, ct))
            return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        try
        {
            var operation = await workspaces.QueueDestroyAsync(workspace.OrganizationId, workspaceId, ct);
            return Results.Accepted($"/api/coding-workspaces/operations/{operation.Id}",
                CodingWorkspaceService.ToResponse(operation));
        }
        catch (CodingWorkspaceException ex) { return ValidationError(ex.Code, ex.Message); }
    }

    [HttpPost("{workspaceId}/observe-connection")]
    public async Task<IResult> RecordConnectionObservation(string workspaceId, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workspaceId, ct);
        if (workspace is null) return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        if (!await CanAccessAsync(workspace.OrganizationId, OrganizationAction.WriteApps, ct))
            return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        try
        {
            var observed = await workspaces.RecordConnectionObservationAsync(workspace.OrganizationId, workspaceId, ct);
            return Results.Ok(CodingWorkspaceService.ToResponse(observed));
        }
        catch (CodingWorkspaceException ex) { return ValidationError(ex.Code, ex.Message); }
    }

    [HttpPost("{workspaceId}/auth")]
    public async Task<IResult> StartAuthentication(
        string workspaceId, StartCodingWorkspaceAuthRequest request, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workspaceId, ct);
        if (workspace is null) return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        if (!await CanAccessAsync(workspace.OrganizationId, OrganizationAction.WriteApps, ct))
            return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        try
        {
            var actor = RequirePrincipal(HttpContext).ToActorIdentity();
            var operation = await workspaces.StartAuthenticationAsync(
                workspace.OrganizationId, workspaceId, request.Provider, actor.Describe(), ct);
            return Results.Accepted($"/api/coding-workspaces/operations/{operation.Id}",
                new CodingWorkspaceAuthOperationResponse(CodingWorkspaceService.ToResponse(operation), request.Provider));
        }
        catch (CodingWorkspaceException ex) { return ValidationError(ex.Code, ex.Message); }
    }

    [HttpPost("{workspaceId}/auth/{provider}/complete")]
    public async Task<IResult> CompleteAuthentication(
        string workspaceId,
        string provider,
        CompleteCodingWorkspaceAuthRequest request,
        CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workspaceId, ct);
        if (workspace is null) return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        if (!await CanAccessAsync(workspace.OrganizationId, OrganizationAction.WriteApps, ct))
            return EndpointResults.NotFound("workspace_not_found", "Coding workspace was not found.");
        try
        {
            var operation = await workspaces.CompleteAuthenticationAsync(
                workspace.OrganizationId, workspaceId, provider, request,
                RequirePrincipal(HttpContext).ToActorIdentity().Describe(), ct);
            return Results.Ok(CodingWorkspaceService.ToResponse(operation));
        }
        catch (CodingWorkspaceException ex) { return ValidationError(ex.Code, ex.Message); }
    }

    private Task<bool> CanAccessAsync(string organizationId, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, action, ct);

    private static IResult ValidationError(string code, string message) =>
        Results.BadRequest(new ErrorResponse(new ErrorBody(code, message, [])));
}
