using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Route("api/reporter/admin")]
[Authorize]
public sealed class ReporterAdminController : ControllerBase
{
    private readonly ITflexDiscoveryService _discovery;

    public ReporterAdminController(ITflexDiscoveryService discovery)
    {
        _discovery = discovery;
    }

    [HttpPost("discovery/{sourceCode}/parameter-groups")]
    public async Task<ActionResult<DiscoveryResultDto>> DiscoverParameterGroups(string sourceCode, CancellationToken ct)
    {
        var result = await _discovery.DiscoverParameterGroupsAsync(sourceCode, ct);
        return Ok(result);
    }

    [HttpPost("bootstrap/project-card-viewer/{sourceCode}")]
    public async Task<ActionResult<BootstrapResultDto>> BootstrapProjectCardViewer(string sourceCode, CancellationToken ct)
    {
        var result = await _discovery.BootstrapProjectCardViewerAsync(sourceCode, ct);
        return Ok(result);
    }
}
