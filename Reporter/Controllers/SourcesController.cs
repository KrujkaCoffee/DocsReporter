using System.Data;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Route("api/reporter/sources")]
[Authorize]
public sealed class SourcesController : ControllerBase
{
    private readonly IReporterSqlConnectionFactory _factory;

    public SourcesController(IReporterSqlConnectionFactory factory)
    {
        _factory = factory;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SourceDto>>> GetSources(CancellationToken ct)
    {
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT SourceId, Code, DisplayName, BaseDocsUrl, IsEnabled FROM app.Source WHERE IsEnabled = 1 ORDER BY DisplayName";

        var list = new List<SourceDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SourceDto(
                r.GetInt32(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetBoolean(4)));
        }

        return Ok(list);
    }

    [HttpGet("{sourceCode}/health")]
    public async Task<ActionResult<object>> Health(string sourceCode, CancellationToken ct)
    {
        try
        {
            await using var cn = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT DB_NAME()";
            var db = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            return Ok(new { sourceCode, ok = true, database = db });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { sourceCode, ok = false, error = ex.Message });
        }
    }
}
