using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Services;
using DocsApi.Reporter.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/reporter/security")]
public sealed class ReporterSecurityController : ControllerBase
{
    private readonly IReporterIdentityService _identity;
    private readonly ITflexAccessPreviewService _accessPreview;
    private readonly ReporterOptions _options;

    public ReporterSecurityController(
        IReporterIdentityService identity,
        ITflexAccessPreviewService accessPreview,
        IOptions<ReporterOptions> options)
    {
        _identity = identity;
        _accessPreview = accessPreview;
        _options = options.Value;
    }

    [HttpGet("me")]
    public async Task<ActionResult<ReporterCurrentUserDto>> Me(
        [FromQuery] string? sources = null,
        CancellationToken ct = default)
    {
        if (IsDisabled())
            return NotFound();

        var result = await _identity.GetCurrentAsync(
            User,
            ParseSources(sources),
            ct);

        return Ok(result);
    }

    [HttpGet("sources/{sourceCode}/identity")]
    public async Task<ActionResult<ReporterSourceIdentityDto>> SourceIdentity(
        string sourceCode,
        CancellationToken ct = default)
    {
        if (IsDisabled())
            return NotFound();

        var result = await _identity.ResolveSourceAsync(
            User,
            sourceCode,
            ct);

        return Ok(result);
    }

    [HttpGet("sources/{sourceCode}/references/{referenceId:int}/preview")]
    public async Task<ActionResult<TflexAccessPreviewDto>> Preview(
        string sourceCode,
        int referenceId,
        [FromQuery] long? objectId = null,
        CancellationToken ct = default)
    {
        if (IsDisabled())
            return NotFound();

        var result = await _accessPreview.PreviewAsync(
            User,
            sourceCode,
            referenceId,
            objectId,
            ct);

        return Ok(result);
    }

    private bool IsDisabled() =>
        _options.SecurityMode.Equals(
            "Disabled",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyCollection<string>? ParseSources(
        string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources))
            return null;

        return sources
            .Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(source => source.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
