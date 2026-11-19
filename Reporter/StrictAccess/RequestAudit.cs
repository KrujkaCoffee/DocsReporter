using System.Data;
using System.Text.Json;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Product;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.StrictAccess;

public sealed class AuditUnavailableException : Exception
{
    public AuditUnavailableException(Exception? inner = null) : base("Журнал действий недоступен.", inner) { }
}
public sealed record AuditAccessDetail(string Source, int ReferenceId, long? ObjectId, long? DocsUserId,
    string Outcome, string Reason, long? AccessObjectId);

// Scoped per HTTP request. The database row exists BEFORE any data action executes.
public sealed class RequestAudit
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly StrictRuntimeOptions _options;
    private readonly List<AuditAccessDetail> _decisions = new();
    private long? _id;
    private string? _label;
    private int? _visibleItems;
    private string _phase = "Started";
    public RequestAudit(IReporterSqlConnectionFactory factory, IOptions<StrictRuntimeOptions> options)
    { _factory = factory; _options = options.Value; }
    public long? Id => _id;
    public void Decision(string source, int reference, long? obj, long? docsUser, StrictDecision decision)
    {
        if (_decisions.Count >= 2048) throw new AuditUnavailableException();
        _decisions.Add(new(source, reference, obj, docsUser, decision.Outcome.ToString(), decision.ReasonCode, decision.AccessObjectId));
    }
    public async Task BeginAsync(HttpContext context, string operation, RuntimeUser? user)
    {
        using var deadline = Deadline();
        try
        {
            await using var cn = await _factory.OpenAppConnectionAsync(deadline.Token);
            await using var cmd = cn.CreateCommand(); cmd.CommandTimeout = Timeout;
            cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM app.ReporterSecuritySchema WHERE Id=1 AND Version=1)
 THROW 51080, N'Нужна миграция строгого журнала.', 1;
INSERT INTO app.ReporterAuditEvent(RequestId,ActorLogin,ActorSid,Operation,ResourceJson,Phase)
OUTPUT inserted.AuditId VALUES(@request,@login,@sid,@operation,@resource,N'Started');";
            P(cmd, "@request", 100, context.TraceIdentifier);
            P(cmd, "@login", 255, user?.Login); P(cmd, "@sid", 184, user?.Sid);
            P(cmd, "@operation", 80, operation);
            // Never serialize headers, raw query strings, credentials or arbitrary client payloads.
            var keys = new[] { "sourceCode", "cardId", "rootId", "referenceId", "objectId" };
            var resource = keys.Where(k => context.Request.RouteValues.ContainsKey(k))
                .ToDictionary(k => k, k => Limit(Convert.ToString(context.Request.RouteValues[k]), 128));
            if (context.Request.Query.TryGetValue("category", out var category) && new[] { "pdf", "dxf", "cad" }.Contains(category.ToString()))
                resource["category"] = category.ToString();
            P(cmd, "@resource", -1, JsonSerializer.Serialize(resource));
            _id = Convert.ToInt64(await cmd.ExecuteScalarAsync(deadline.Token));
        }
        catch (Exception ex) { throw new AuditUnavailableException(ex); }
    }
    public void Describe(object? body)
    {
        switch (body)
        {
            case ProductCardRootsDto card: _label = card.Card.Code; _visibleItems = card.Roots.Count; break;
            case ProductLevelDto level: _label = level.Parent.Denotation; _visibleItems = level.Page.Items.Count; break;
            case ProductFileRootsDto roots: _label = roots.Owner.Denotation + " / " + roots.Title; _visibleItems = roots.Roots.Count; break;
            case ProductFilePageDto page: _label = page.Folder.Name; _visibleItems = page.Page.Items.Count; break;
            case ProductPageDto<ProductCardDto> cards: _visibleItems = cards.Items.Count; break;
        }
    }
    public Task ReadyAsync(int status) => SaveAsync("ReadyToReturn", status, null);
    public Task CompleteAsync(int status, string? failure = null) => SaveAsync(
        failure is not null ? "Failed" : status >= 400 ? "DeniedOrFailed" : "Completed", status, failure);
    private async Task SaveAsync(string phase, int status, string? failure)
    {
        if (_id is null) throw new AuditUnavailableException();
        using var deadline = Deadline();
        try
        {
            await using var cn = await _factory.OpenAppConnectionAsync(deadline.Token);
            await using var cmd = cn.CreateCommand(); cmd.CommandTimeout = Timeout;
            cmd.CommandText = @"
UPDATE app.ReporterAuditEvent SET Phase=@phase,HttpStatus=@status,ResourceLabel=@label,
 VisibleItemCount=@count,DecisionsJson=@decisions,FailureCode=@failure,
 ReadyUtc=CASE WHEN @phase=N'ReadyToReturn' THEN SYSUTCDATETIME() ELSE ReadyUtc END,
 FinishedUtc=CASE WHEN @phase<>N'ReadyToReturn' THEN SYSUTCDATETIME() ELSE FinishedUtc END
WHERE AuditId=@id;
IF @@ROWCOUNT<>1 THROW 51081,N'Строка журнала не найдена.',1;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = _id.Value });
            cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.Int) { Value = status });
            cmd.Parameters.Add(new SqlParameter("@count", SqlDbType.Int) { Value = (object?)_visibleItems ?? DBNull.Value });
            P(cmd, "@phase", 32, phase); P(cmd, "@label", 512, Limit(_label, 512));
            P(cmd, "@decisions", -1, JsonSerializer.Serialize(_decisions));
            P(cmd, "@failure", 100, failure);
            await cmd.ExecuteNonQueryAsync(deadline.Token);
            _phase = phase;
        }
        catch (Exception ex) { throw new AuditUnavailableException(ex); }
    }
    public string CurrentPhase => _phase;
    private int Timeout => Math.Clamp(_options.AuditTimeoutSeconds, 1, 15);
    private CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(Timeout));
    private static string? Limit(string? value, int max) => value is null ? null : value[..Math.Min(value.Length, max)];
    private static void P(SqlCommand cmd, string name, int size, string? value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size) { Value = (object?)value ?? DBNull.Value });
}

// Failure to durably record the decision BEFORE serialization prevents a successful response.
public sealed class StrictAuditResultFilter : IAsyncResultFilter
{
    private readonly RequestAudit _audit;
    public StrictAuditResultFilter(RequestAudit audit) => _audit = audit;
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (_audit.Id is null) throw new AuditUnavailableException();
        var status = context.Result is ObjectResult obj ? obj.StatusCode ?? 200 : context.HttpContext.Response.StatusCode;
        if (context.Result is ObjectResult value) _audit.Describe(value.Value);
        await _audit.ReadyAsync(status);
        await next();
    }
}
