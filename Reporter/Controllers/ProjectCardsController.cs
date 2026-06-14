using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Route("api/reporter/sources/{sourceCode}/project-cards")]
//[Authorize]
public sealed class ProjectCardsController : ControllerBase
{
    private readonly IProjectCardExplorerService _projectCards;
    private readonly IProjectCardFileExplorerService _files;

    public ProjectCardsController(
        IProjectCardExplorerService projectCards,
        IProjectCardFileExplorerService files)
    {
        _projectCards = projectCards;
        _files = files;
    }

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<ProjectCardSearchItemDto>>> Search(
        string sourceCode,
        [FromQuery] string? query,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _projectCards.SearchAsync(User, sourceCode, query, page, pageSize, ct);
        return Ok(result);
        //try
        //{
        //    var result = await _projectCards.SearchAsync(User, sourceCode, query, page, pageSize, ct);
        //    return Ok(result);
        //}
        //catch (UnauthorizedAccessException)
        //{
        //    return Forbid();
        //}
    }

    [HttpGet("{objectId:long}/full-card")]
    public async Task<ActionResult<ProjectCardFullDto>> FullCard(
        string sourceCode,
        long objectId,
        [FromQuery] int depth = 1,
        [FromQuery] int fileDepth = 4,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _projectCards.GetFullCardAsync(User, sourceCode, objectId, depth, fileDepth, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpGet("{objectId:long}/file-categories")]
    public async Task<ActionResult<IReadOnlyList<FileCategoryDto>>> FileCategories(
        string sourceCode,
        long objectId,
        [FromQuery] int fileDepth = 4,
        [FromQuery] int maxItems = 500,
        CancellationToken ct = default)
    {
        var result = await _files.GetFileCategoriesAsync(User, sourceCode, objectId, fileDepth, maxItems, ct);
        return Ok(result);
    }
}
