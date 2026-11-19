using System.Data;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Services;
using DocsApi.Reporter.StrictAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace DocsApi.Reporter.Controllers;

[ApiController]
[Authorize]
[Route("api/reporter/strict")]
public sealed class StrictSystemController : ControllerBase
{
    private readonly ITflexAccessDecisionService _access;
    private readonly IReporterSqlConnectionFactory _factory;
    public StrictSystemController(ITflexAccessDecisionService access, IReporterSqlConnectionFactory factory)
    { _access = access; _factory = factory; }

    [HttpGet("me")]
    public IActionResult Me() => Ok(new { name = User.Identity?.Name, authenticated = true });

    // System-only, own Windows identity only. No userId, login or SID impersonation parameter.
    [HttpGet("sources/{sourceCode}/references/{referenceId:int}/objects/{objectId:long}/decision")]
    public async Task<IActionResult> Decision(string sourceCode, int referenceId, long objectId, CancellationToken ct)
    {
        var result = await _access.CanReadAsync(User, sourceCode, referenceId, objectId, ct);
        return Ok(result);
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] long after = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        if (after < 0 || take < 1 || take > 200) return BadRequest(new { error = "Некорректный диапазон журнала." });
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand(); cmd.CommandTimeout = 15;
        cmd.CommandText = @"
SELECT TOP (@take) AuditId,StartedUtc,ReadyUtc,FinishedUtc,RequestId,ActorLogin,Operation,ResourceLabel,
 ResourceJson,Phase,HttpStatus,VisibleItemCount,DecisionsJson,FailureCode
FROM app.ReporterAuditEvent WHERE AuditId>@after ORDER BY AuditId;";
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
        cmd.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
        var rows = new List<Dictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return Ok(rows);
    }
}
