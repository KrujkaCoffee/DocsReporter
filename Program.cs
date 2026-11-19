using DocsApi.Reporter.StrictAccess;
using DocsApi.Reporter.Product;
using DocsApi.Configurations;
using DocsApi.Data;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options => options.Filters.AddService<StrictAuditResultFilter>());
builder.Services.Configure<StrictRuntimeOptions>(builder.Configuration.GetSection("Reporter:StrictRuntime"));
builder.Services.AddScoped<RequestAudit>();
builder.Services.AddScoped<StrictAuditResultFilter>();
builder.Services.Configure<ProductExplorerOptions>(builder.Configuration.GetSection("Reporter:ProductExplorer"));
builder.Services.AddScoped<ProductExplorerService>();
builder.Services.AddScoped<ProductPilotFilter>();

builder.Services.Configure<FileSettings>(
    builder.Configuration.GetSection("FileServerSettings"));

builder.Services.Configure<ProcessTkpRefs>(
    builder.Configuration.GetSection("ProcessTkpRefs"));

// Reporter
builder.Services.Configure<ReporterOptions>(
    builder.Configuration.GetSection("Reporter"));

builder.Services.AddScoped<IReporterSqlConnectionFactory, ReporterSqlConnectionFactory>();
builder.Services.AddScoped<IReporterAccessService, ReporterAccessService>();
builder.Services.AddScoped<ITflexDiscoveryService, TflexDiscoveryService>();
builder.Services.AddScoped<IProjectCardExplorerService, ProjectCardExplorerService>();
builder.Services.AddScoped<IProjectCardFileExplorerService, ProjectCardFileExplorerService>();
builder.Services.AddScoped<IFederatedProjectCardSearchService, FederatedProjectCardSearchService>();
builder.Services.AddScoped<IReporterIdentityService, ReporterIdentityService>();
builder.Services.AddScoped<ITflexAccessPreviewService, TflexAccessPreviewService>();
// Legacy heuristic service is deliberately NOT registered.
builder.Services.AddScoped<StrictReadService>();
builder.Services.AddScoped<ITflexAccessDecisionService>(sp => sp.GetRequiredService<StrictReadService>());
builder.Services.AddScoped<IStrictReferenceGate>(sp => sp.GetRequiredService<StrictReadService>());
builder.Services.AddScoped<IReporterAdminCatalogService, ReporterAdminCatalogService>();
builder.Services.AddScoped<IReporterReferenceBrowserService, ReporterReferenceBrowserService>();

builder.Services.AddDbContext<AppDbContext1>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("srv-docs-pkb")));

builder.Services.AddDbContext<AppDbContext2>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("srv-tdocs")));

builder.Services.AddDbContext<AppDbContext3>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("srv-docs")));

// Windows Auth / Negotiate
builder.Services
    .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build();
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Docs API", Version = "v1" });
    c.EnableAnnotations();
});

var app = builder.Build();

// New binary has no anonymous/Preview opt-out. Fail early on obsolete debug settings.
var oldOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReporterOptions>>().Value;
var productOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProductExplorerOptions>>().Value;
if (!string.IsNullOrWhiteSpace(oldOptions.DebugWindowsLogin) || !string.IsNullOrWhiteSpace(oldOptions.DebugWindowsSid)
    || productOptions.AllowAnonymousInDevelopment)
    throw new InvalidOperationException("Удалите DebugWindowsLogin/DebugWindowsSid и отключите AllowAnonymousInDevelopment перед запуском строгого режима.");

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
// Before static files, authorization, MVC and any legacy endpoint.
// [AllowAnonymous] on an old controller cannot bypass this boundary.
app.UseMiddleware<StrictPerimeterMiddleware>();
app.UseAuthorization();
app.UseStaticFiles();
app.MapControllers();
// Root aliases are redirected by the protected perimeter. Swagger and legacy pages are not exposed.
app.Run();
