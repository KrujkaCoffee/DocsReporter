using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Product;

public sealed class ProductPilotFilter : IAsyncActionFilter
{
    private readonly ProductExplorerOptions _options;
    private readonly IWebHostEnvironment _environment;
    public ProductPilotFilter(IOptions<ProductExplorerOptions> options, IWebHostEnvironment environment)
    { _options = options.Value; _environment = environment; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        context.HttpContext.Response.Headers["Cache-Control"] = "no-store";
        if (!_options.Enabled)
        {
            context.Result = new NotFoundObjectResult(new { error = "Просмотр изделия не включён в конфигурации." }); return;
        }
        // Этот этап проверяет данные, но ещё не является промышленной границей безопасности.
        // Никакие тестовые обходы и существующий экспериментальный ACL не выпускаем в Production.
        if (!_environment.IsDevelopment())
        {
            context.Result = new ObjectResult(new { error = "Этап проверки изделия доступен только в Development. Промышленный доступ будет включён после этапа WinAuth." }) { StatusCode = 503 }; return;
        }
        if (context.HttpContext.User.Identity?.IsAuthenticated != true && !_options.AllowAnonymousInDevelopment)
        {
            context.Result = new ChallengeResult(); return;
        }
        await next();
    }
}
