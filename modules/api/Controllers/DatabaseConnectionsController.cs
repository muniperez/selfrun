using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Selfrun.Api.Auth;
using Selfrun.Common.Services;
using static Selfrun.Api.Controllers.EndpointResults;

namespace Selfrun.Api.Controllers;

[ApiController, Authorize, Route("api/databases/{id}/connection")]
public sealed class DatabaseConnectionsController(DatabaseConnectionService service) : ControllerBase
{
    [HttpPost("reveal")]
    public Task<IResult> Reveal(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        return DatabasesController.Handle(async () => Results.Ok(await service.RevealAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), ct)));
    }
    [HttpPost("/api/databases/{id}/backup-recovery-material")]
    public Task<IResult> ExportRecoveryMaterial(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        return DatabasesController.Handle(async () => Results.Ok(await service.ExportRecoveryMaterialAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), ct)));
    }
}
