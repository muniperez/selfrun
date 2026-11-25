// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Selfrun.Common.Contracts;
using Selfrun.Common.Services;
using Selfrun.Api.Services;

namespace Selfrun.Api.Controllers;

[ApiController]
[Route("api/instance")]
public sealed class InstanceController(IOptions<ServerReleaseOptions> release) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public InstanceInfoResponse Get([FromServices] IOptions<ManagedDomainOptions> options) =>
        new("selfrun", 1, options.Value.Suffix,
            release.Value.Metadata?.ServerVersion,
            release.Value.Metadata?.SourceRevision,
            release.Value.Metadata?.CliCompatibility);
}
