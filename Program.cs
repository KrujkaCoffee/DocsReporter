using DocsApi.Configurations;
using DocsApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi.Models;
using System.Runtime;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using DocsApi.Reporter.Services;




var builder = WebApplication.CreateBuilder(args);


// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<FileSettings>(builder.Configuration.GetSection("FileServerSettings"));  
builder.Services.Configure<ProcessTkpRefs>(builder.Configuration.GetSection("ProcessTkpRefs"));

// reporter подключение ++
builder.Services.Configure<ReporterOptions>(
    builder.Configuration.GetSection("Reporter"));

builder.Services.AddScoped<IReporterSqlConnectionFactory, ReporterSqlConnectionFactory>();
builder.Services.AddScoped<IReporterAccessService, ReporterAccessService>();
builder.Services.AddScoped<ITflexDiscoveryService, TflexDiscoveryService>();
builder.Services.AddScoped<IProjectCardExplorerService, ProjectCardExplorerService>();

builder.Services.AddDbContext<AppDbContext1>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("srv-docs-pkb")));
builder.Services.AddDbContext<AppDbContext2>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("srv-tdocs")));
builder.Services.AddDbContext<AppDbContext3>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("srv-docs")));

builder.Services.AddControllers();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Docs API", Version = "v1" });
    c.EnableAnnotations(); 
});
var app = builder.Build();


app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseRouting();

app.MapControllers();

app.Run();
