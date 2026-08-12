using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/reporter/references")]
public sealed class ReporterReferencesController : ControllerBase
{
    private readonly IReporterReferenceBrowserService _references;

    public ReporterReferencesController(IReporterReferenceBrowserService references)
    {
        _references = references;
    }

    [HttpGet("catalog")]
    public async Task<ActionResult<IReadOnlyList<ReporterReferenceCatalogItemDto>>> Catalog(
        [FromQuery] string? sources = null,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _references.GetCatalogAsync(User, ParseSources(sources), ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("sources/{sourceCode}/groups/{groupId:int}/schema")]
    public async Task<ActionResult<ReporterReferenceSchemaDto>> Schema(
        string sourceCode,
        int groupId,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _references.GetSchemaAsync(User, sourceCode, groupId, ct));
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

    [HttpGet("sources/{sourceCode}/groups/{groupId:int}/items")]
    public async Task<ActionResult<ReporterReferenceItemsPageDto>> Items(
        string sourceCode,
        int groupId,
        [FromQuery] string? query = null,
        [FromQuery] long? parentId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _references.GetItemsAsync(
                User,
                sourceCode,
                groupId,
                query,
                parentId,
                page,
                pageSize,
                ct));
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

    [HttpGet("sources/{sourceCode}/groups/{groupId:int}/items/{objectId:long}")]
    public async Task<ActionResult<ReporterReferenceObjectDto>> Object(
        string sourceCode,
        int groupId,
        long objectId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _references.GetObjectAsync(User, sourceCode, groupId, objectId, ct);
            return result is null ? NotFound() : Ok(result);
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

    private static IReadOnlyCollection<string>? ParseSources(string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources))
            return null;

        return sources
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
