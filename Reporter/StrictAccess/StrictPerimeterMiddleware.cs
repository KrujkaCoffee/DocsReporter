using DocsApi.Reporter.Controllers;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.StrictAccess;

public sealed class StrictPerimeterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<StrictPerimeterMiddleware> _logger;
    private static readonly HashSet<string> Assets = new(StringComparer.OrdinalIgnoreCase)
    {
        "/reporter/product.html", "/reporter/product.css", "/reporter/product.js", "/reporter/product-model.js"
    };
    public StrictPerimeterMiddleware(RequestDelegate next, ILogger<StrictPerimeterMiddleware> logger)
    { _next = next; _logger = logger; }

    public async Task InvokeAsync(HttpContext context, RequestAudit audit, IReporterAccessService publication,
        IOptions<StrictRuntimeOptions> options)
    {
        context.Response.Headers["Cache-Control"] = "no-store, private";
        context.Response.Headers["Vary"] = "Authorization";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
        var user = RuntimePrincipal.Read(context.User);
        var path = context.Request.Path.Value ?? "";
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var product = action?.ControllerTypeInfo.AsType() == typeof(ReporterProductController);
        var system = action?.ControllerTypeInfo.AsType() == typeof(StrictSystemController);
        var asset = Assets.Contains(path);
        var redirect = new[] { "/", "/reporter", "/reporter/", "/reporter/index.html", "/reporter/product" }.Contains(path, StringComparer.OrdinalIgnoreCase);
        var operation = product ? "Product." + action!.ActionName : system ? "System." + action!.ActionName : "Route.Refused";
        var isData = !asset && !redirect;
        try
        {
            if (isData || user is null) await audit.BeginAsync(context, operation, user);
            if (context.User.Identity?.IsAuthenticated != true)
            {
                if (audit.Id is not null) await audit.CompleteAsync(401, "windows_auth_required");
                await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme); return;
            }
            if (user is null)
            {
                if (audit.Id is not null) await audit.CompleteAsync(403, "windows_sid_required");
                await Error(context, 403, "Не удалось подтвердить учётную запись Windows."); return;
            }
            // All safe shell/redirect requests also honor the reporter account's enabled state.
            await publication.EnsureAppUserAsync(context.User, context.RequestAborted);
            if (asset || redirect)
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                { await Error(context, 405, "Метод недоступен."); return; }
                if (redirect)
                {
                    context.Response.Redirect(context.Request.PathBase + "/reporter/product.html" + context.Request.QueryString);
                    return;
                }
                await _next(context); return;
            }
            if ((!product && !system) || !HttpMethods.IsGet(context.Request.Method))
            {
                await audit.CompleteAsync(410, "legacy_route_not_migrated");
                await Error(context, 410, "Этот маршрут недоступен в строгом режиме."); return;
            }
            if (system && action!.ActionName != nameof(StrictSystemController.Me) && !RuntimePrincipal.IsAdministrator(user, options.Value))
            {
                await audit.CompleteAsync(403, "system_admin_required");
                await Error(context, 403, "Доступ запрещён."); return;
            }
            await _next(context);
            if (audit.Id is not null) await audit.CompleteAsync(context.Response.StatusCode);
        }
        catch (AuditUnavailableException ex)
        {
            _logger.LogError(ex, "AUDIT_UNAVAILABLE: request {RequestId}, audit {AuditId}, phase {Phase}", context.TraceIdentifier, audit.Id, audit.CurrentPhase);
            if (!context.Response.HasStarted) await Error(context, 503, "Сервис временно недоступен. Данные не выданы.");
            else context.Abort();
        }
        catch (UnauthorizedAccessException)
        {
            await TryFinish(audit, 403, "reporter_user_disabled_or_conflicting", context);
            if (!context.Response.HasStarted) await Error(context, 403, "Доступ запрещён."); else context.Abort();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { await TryFinish(audit, 499, "request_cancelled", context); context.Abort(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка защищённого запроса {RequestId}", context.TraceIdentifier);
            await TryFinish(audit, 503, "request_failed", context);
            if (!context.Response.HasStarted) await Error(context, 503, "Не удалось выполнить запрос."); else context.Abort();
        }
    }
    private async Task TryFinish(RequestAudit audit, int status, string reason, HttpContext context)
    {
        try
        {
            if (audit.Id is null) await audit.BeginAsync(context, "Session.Refused", RuntimePrincipal.Read(context.User));
            await audit.CompleteAsync(status, reason);
        }
        catch (Exception ex) { _logger.LogError(ex, "AUDIT_COMPLETION_FAILED {RequestId}/{AuditId}", context.TraceIdentifier, audit.Id); }
    }
    private static async Task Error(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message, requestId = context.TraceIdentifier });
    }
}
