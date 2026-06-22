using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Route("api/reporter/project-cards")]
//[Authorize]
public sealed class FederatedProjectCardsController : ControllerBase
{
    private readonly IFederatedProjectCardSearchService _search;

    public FederatedProjectCardsController(IFederatedProjectCardSearchService search)
    {
        _search = search;
    }

    /// <summary>
    /// Searches project cards across several configured T-FLEX sources in one request.
    /// A failed or timed-out source is returned as a partial result and does not fail the whole search.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<FederatedProjectCardSearchDto>> Search(
        [FromQuery] string query,
        [FromQuery] string? sources = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] int? sourceTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        try
        {
            var sourceCodes = ParseSources(sources);
            var result = await _search.SearchAsync(
                User,
                query,
                sourceCodes,
                page,
                pageSize,
                sourceTimeoutSeconds,
                ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    private static IReadOnlyCollection<string>? ParseSources(string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources))
            return null;

        return sources
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(source => source.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
