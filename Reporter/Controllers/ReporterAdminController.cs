using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/reporter/admin")]
public sealed class ReporterAdminController : ControllerBase
{
    private readonly ITflexDiscoveryService _discovery;
    private readonly IReporterAdminCatalogService _admin;

    public ReporterAdminController(
        ITflexDiscoveryService discovery,
        IReporterAdminCatalogService admin)
    {
        _discovery = discovery;
        _admin = admin;
    }

    [HttpPost("discovery/{sourceCode}/parameter-groups")]
    public async Task<ActionResult<DiscoveryResultDto>> DiscoverParameterGroups(
        string sourceCode,
        CancellationToken ct)
    {
        try
        {
            await _admin.EnsureAdminAsync(User, ct);
            var result = await _discovery.DiscoverParameterGroupsAsync(sourceCode, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPost("bootstrap/project-card-viewer/{sourceCode}")]
    public async Task<ActionResult<BootstrapResultDto>> BootstrapProjectCardViewer(
        string sourceCode,
        CancellationToken ct)
    {
        try
        {
            await _admin.EnsureAdminAsync(User, ct);
            var result = await _discovery.BootstrapProjectCardViewerAsync(sourceCode, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<SourceDto>>> Sources(CancellationToken ct)
    {
        try
        {
            await _admin.EnsureAdminAsync(User, ct);
            return Ok(await _admin.GetSourcesAsync(ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("roles")]
    public async Task<ActionResult<IReadOnlyList<ReporterAdminRoleDto>>> Roles(CancellationToken ct)
    {
        try
        {
            return Ok(await _admin.GetRolesAsync(User, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("overview")]
    public async Task<ActionResult<ReporterAdminOverviewDto>> Overview(
        [FromQuery] string sourceCode,
        [FromQuery] string roleCode = "ProjectCardViewer",
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _admin.GetOverviewAsync(User, sourceCode, roleCode, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("sources/{sourceCode}/groups")]
    public async Task<ActionResult<IReadOnlyList<ReporterAdminGroupPolicyDto>>> Groups(
        string sourceCode,
        [FromQuery] string roleCode = "ProjectCardViewer",
        [FromQuery] string? query = null,
        [FromQuery] bool enabledOnly = false,
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _admin.GetGroupsAsync(User, sourceCode, roleCode, query, enabledOnly, take, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("sources/{sourceCode}/groups/{groupId:int}")]
    public async Task<ActionResult<ReporterAdminGroupPolicyDto>> Group(
        string sourceCode,
        int groupId,
        [FromQuery] string roleCode = "ProjectCardViewer",
        CancellationToken ct = default)
    {
        try
        {
            var result = await _admin.GetGroupAsync(User, sourceCode, roleCode, groupId, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPut("sources/{sourceCode}/groups/{groupId:int}/policy")]
    public async Task<ActionResult<ReporterAdminGroupPolicyDto>> UpdateGroupPolicy(
        string sourceCode,
        int groupId,
        [FromQuery] string? roleCode,
        [FromBody] ReporterAdminGroupPolicyUpdateDto request,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _admin.UpdateGroupPolicyAsync(User, sourceCode, string.IsNullOrWhiteSpace(roleCode) ? "ProjectCardViewer" : roleCode, groupId, request, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("sources/{sourceCode}/relations")]
    public async Task<ActionResult<IReadOnlyList<ReporterAdminRelationPolicyDto>>> Relations(
        string sourceCode,
        [FromQuery] string roleCode = "ProjectCardViewer",
        [FromQuery] int? groupId = null,
        [FromQuery] string? query = null,
        [FromQuery] bool enabledOnly = false,
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _admin.GetRelationsAsync(User, sourceCode, roleCode, groupId, query, enabledOnly, take, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("sources/{sourceCode}/relations/{relationGroupId:int}")]
    public async Task<ActionResult<ReporterAdminRelationPolicyDto>> Relation(
        string sourceCode,
        int relationGroupId,
        [FromQuery] string roleCode = "ProjectCardViewer",
        CancellationToken ct = default)
    {
        try
        {
            var result = await _admin.GetRelationAsync(User, sourceCode, roleCode, relationGroupId, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPut("sources/{sourceCode}/relations/{relationGroupId:int}/policy")]
    public async Task<ActionResult<ReporterAdminRelationPolicyDto>> UpdateRelationPolicy(
        string sourceCode,
        int relationGroupId,
        [FromQuery] string? roleCode,
        [FromBody] ReporterAdminRelationPolicyUpdateDto request,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _admin.UpdateRelationPolicyAsync(User, sourceCode, string.IsNullOrWhiteSpace(roleCode) ? "ProjectCardViewer" : roleCode, relationGroupId, request, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
