using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Selfrun.Api.Auth;
using Selfrun.Common.Contracts;
using Selfrun.Common.Services;
using static Selfrun.Api.Controllers.EndpointResults;

namespace Selfrun.Api.Controllers;

[ApiController, Authorize, Route("api/databases/{id}/backups")]
public sealed class DatabaseBackupPurgeController(DatabaseBackupPurgeService purge) : ControllerBase
{
    [HttpDelete]
    public Task<IResult> Purge(string id, [FromBody] DatabaseBackupPurgeRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return DatabasesController.Handle(async () =>
        {
            var operation = await purge.QueueAsync(id, request, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
            return Results.Accepted($"/api/databases/{id}/operations", new { operation.Id, operation.Kind, operation.Status });
        });
    }
}
