using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Selfrun.Api.Auth;
using Selfrun.Api.Services;
using Selfrun.Common.Data;
using Selfrun.Common.Services;
using Selfrun.Common.Contracts;
using static Selfrun.Api.Controllers.EndpointResults;

namespace Selfrun.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/environments/{appId}/storage")]
public sealed class AppStorageController : ControllerBase
{
    // Registration is not a live provider/mount health check. Include retained volumes.
    [HttpGet]
    public async Task<IResult> List(string appId, [FromServices] SelfrunDbContext db,
        [FromServices] ControlPlaneAuthorization authorization, CancellationToken ct,
        [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, ct);
        if (ownershipError is not null) { return ownershipError; }
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.ReadApps, ct))
        { return EndpointResults.Forbidden(); }
        var volumes = await db.AppStorageVolumes.AsNoTracking().Where(x => x.AppId == appId).OrderBy(x => x.Name).ToListAsync(ct);
        return Results.Ok(volumes.Select(x => new AppStorageVolumeResponse(x.Name, "hetzner", x.ProviderVolumeId,
            x.Region, x.SizeGb, x.Uid, x.Gid,
            x.ProviderVolumeId is not null ? "registered" : x.CreationAttempted ? "creation_unresolved" : "pending")));
    }
}
