using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Data;

namespace Selfrun.Api.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("ready")]
    public async Task<IResult> Ready([FromServices] SelfrunDbContext db, CancellationToken cancellationToken)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);
        return canConnect ? Results.Ok(new { status = "ready" }) : Results.Problem("Database is not reachable.", statusCode: 503);
    }
}
