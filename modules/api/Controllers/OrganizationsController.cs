using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Selfrun.Api.Auth;
using Selfrun.Common.Data;
using Selfrun.Common.Contracts;
using Selfrun.Common.Models;
using Selfrun.Api.Providers;
using Selfrun.Api.Services;
using Selfrun.Common.Services;
using Selfrun.Common.Validation;
using static Selfrun.Api.Controllers.EndpointResults;

namespace Selfrun.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class OrganizationsController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> ListOrganizations([FromServices] SelfrunDbContext db, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return EndpointResults.Forbidden();
        }

        var organizations = await db.OrganizationMembers
            .Include(x => x.Organization)
            .Where(x => x.UserId == principal.UserId)
            .Select(x => x.Organization.ToResponse())
            .ToListAsync(cancellationToken);

        return Results.Ok(organizations);
    }

    [HttpPost]
    public async Task<IResult> CreateOrganization([FromBody] CreateOrganizationRequest request, [FromServices] SelfrunDbContext db, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return EndpointResults.Forbidden();
        }

        var validation = ControlPlaneValidation.ValidateOrganization(request);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("Organization request is invalid.", validation);
        }

        if (await db.Organizations.AnyAsync(x => x.Slug.ToLower() == request.Slug.ToLower(), cancellationToken))
        {
            return EndpointResults.Conflict("organization_slug_taken", "Organization slug is already taken.");
        }

        var now = timeProvider.GetUtcNow();
        var organization = new Organization
        {
            Id = ids.NewId("org"),
            Name = request.Name,
            Slug = request.Slug,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = ids.NewId("mem"),
            OrganizationId = organization.Id,
            UserId = principal.UserId,
            Role = OrganizationRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/organizations/{organization.Id}", organization.ToResponse());
    }

    [HttpGet("{organizationId}")]
    public async Task<IResult> GetOrganization([FromRoute] string organizationId, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var organization = await db.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken);
        return organization is null ? EndpointResults.NotFound("organization_not_found", "Organization was not found.") : Results.Ok(organization.ToResponse());
    }
}
