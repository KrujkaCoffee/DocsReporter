using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Route("api/reporter/sources/{sourceCode}/project-cards")]
//[Authorize]
public sealed class ProjectCardsController : ControllerBase
{
    private readonly IProjectCardExplorerService _projectCards;

    public ProjectCardsController(IProjectCardExplorerService projectCards)
    {
        _projectCards = projectCards;
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
        CancellationToken ct = default)
    {
        try
        {
            var result = await _projectCards.GetFullCardAsync(User, sourceCode, objectId, depth, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }
}
