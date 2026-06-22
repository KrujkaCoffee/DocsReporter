using DocsApi.Configurations;
using DocsApi.Data;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using DocsApi.Reporter.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

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

builder.Services.AddAuthorization();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Docs API", Version = "v1" });
    c.EnableAnnotations();
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

// Reporter UI is a static, read-only shell over the reporter API.
// Open: /reporter or /reporter/index.html
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// ВАЖНО: до MapControllers
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/", () => Results.Redirect("/reporter/index.html"));
app.MapGet("/reporter", () => Results.Redirect("/reporter/index.html"));

app.Run();
