using System.Data;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/reporter/ui")]
public sealed class ReporterUiController : ControllerBase
{
    private readonly IReporterSqlConnectionFactory _factory;

    public ReporterUiController(IReporterSqlConnectionFactory factory)
    {
        _factory = factory;
    }

    [HttpGet("config")]
    public ActionResult<object> Config()
    {
        return Ok(new
        {
            appTitle = "Docs Reporter",
            defaultDepth = 2,
            defaultFileDepth = 4,
            defaultPageSize = 50,
            maxPageSize = 100,
            uiVersion = "stage-3"
        });
    }

    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<SourceDto>>> Sources(CancellationToken ct = default)
    {
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandType = CommandType.Text;
        cmd.CommandText = @"
SELECT SourceId, Code, DisplayName, BaseDocsUrl, IsEnabled
FROM app.Source
WHERE IsEnabled = 1
ORDER BY DisplayName, Code;";

        var result = new List<SourceDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SourceDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)));
        }

        return Ok(result);
    }
}
