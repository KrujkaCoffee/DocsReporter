using DocsApi.Reporter.Product;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Route("api/reporter/product")]
[ServiceFilter(typeof(ProductPilotFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReporterProductController : ControllerBase
{
    private readonly ProductExplorerService _service;
    private readonly ILogger<ReporterProductController> _logger;
    public ReporterProductController(ProductExplorerService service, ILogger<ReporterProductController> logger)
    { _service = service; _logger = logger; }

    [HttpGet("sources")]
    public Task<ActionResult<IReadOnlyList<ProductSourceDto>>> Sources() => Run(ct => _service.SourcesAsync(ct));

    [HttpGet("sources/{sourceCode}/cards")]
    public Task<ActionResult<ProductPageDto<ProductCardDto>>> Search(string sourceCode,
        [FromQuery] string? query, [FromQuery] long after = 0) =>
        Run(ct => _service.SearchAsync(User, sourceCode, query, after, ct));

    [HttpGet("sources/{sourceCode}/cards/{cardId:long}/roots")]
    public Task<ActionResult<ProductCardRootsDto>> Roots(string sourceCode, long cardId, [FromQuery] string? asOf) =>
        Run(ct => _service.RootsAsync(User, sourceCode, cardId, asOf, ct));

    [HttpGet("sources/{sourceCode}/cards/{cardId:long}/roots/{rootId:long}/children")]
    public Task<ActionResult<ProductLevelDto>> Children(string sourceCode, long cardId, long rootId,
        [FromQuery] string? path, [FromQuery] string? asOf, [FromQuery] long after = 0) =>
        Run(ct => _service.ChildrenAsync(User, sourceCode, cardId, rootId, path, asOf, after, ct));

    [HttpGet("sources/{sourceCode}/cards/{cardId:long}/roots/{rootId:long}/file-roots")]
    public Task<ActionResult<ProductFileRootsDto>> FileRoots(string sourceCode, long cardId, long rootId,
        [FromQuery] string category = "pdf", [FromQuery] string? path = null, [FromQuery] string? asOf = null) =>
        Run(ct => _service.FileRootsAsync(User, sourceCode, cardId, rootId, category, path, asOf, ct));

    [HttpGet("sources/{sourceCode}/cards/{cardId:long}/roots/{rootId:long}/files")]
    public Task<ActionResult<ProductFilePageDto>> Files(string sourceCode, long cardId, long rootId,
        [FromQuery] string? folderPath, [FromQuery] string category = "pdf", [FromQuery] string? path = null,
        [FromQuery] string? asOf = null, [FromQuery] long after = 0) =>
        Run(ct => _service.FilesAsync(User, sourceCode, cardId, rootId, category, path, folderPath, asOf, after, ct));

    private async Task<ActionResult<T>> Run<T>(Func<CancellationToken, Task<T>> operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { return Ok(await operation(timeout.Token)); }
        catch (ProductExplorerProblem ex) { return StatusCode(ex.Status, new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        { return StatusCode(504, new { error = "Превышено время загрузки. Сузьте запрос или повторите позже." }); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка просмотра изделия. Источник {Source}; запрос {RequestId}",
                RouteData.Values["sourceCode"], HttpContext.TraceIdentifier);
            return StatusCode(503, new { error = "Загрузка недоступна. Проверьте профиль и схему источника в журнале сервера.", requestId = HttpContext.TraceIdentifier });
        }
    }
}
