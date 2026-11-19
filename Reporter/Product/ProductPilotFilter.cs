using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Product;

// Class name retained for DI compatibility; no Development/anonymous bypass remains.
public sealed class ProductPilotFilter : IAsyncActionFilter
{
    private readonly ProductExplorerOptions _options;
    public ProductPilotFilter(IOptions<ProductExplorerOptions> options) => _options = options.Value;
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!_options.Enabled)
        { context.Result = new NotFoundObjectResult(new { error = "Раздел недоступен." }); return; }
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        { context.Result = new ChallengeResult(); return; }
        await next();
    }
}
