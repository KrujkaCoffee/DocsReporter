using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Product;
using DocsApi.Reporter.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.StrictAccess;

public interface IStrictReferenceGate
{
    Task RequireReadyAsync(ClaimsPrincipal user, string sourceCode, int referenceId, CancellationToken ct);
}

public sealed class StrictReadService : ITflexAccessDecisionService, IStrictReferenceGate
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _publication;
    private readonly ProductExplorerOptions _products;
    private readonly StrictRuntimeOptions _options;
    private readonly RequestAudit _audit;
    private readonly ILogger<StrictReadService> _logger;
    // Request-scoped only: no rights or identity data is shared across HTTP requests/users/sources.
    private readonly Dictionary<(string, string, int), StrictDecision> _referenceGates = new();
    private readonly Dictionary<(string, string), SourceSnapshot> _sources = new();
    private readonly Dictionary<(string, int), StrictAclSnapshot> _acl = new();
    private readonly Dictionary<(string, string, int, long), TflexAccessDecisionDto> _decisions = new();
    public bool ShouldEnforceAppPolicy => true;
    public bool ShouldEnforceTflexRead => true;

    public StrictReadService(IReporterSqlConnectionFactory factory, IReporterAccessService publication,
        IOptions<ProductExplorerOptions> products, IOptions<StrictRuntimeOptions> options,
        RequestAudit audit, ILogger<StrictReadService> logger)
    { _factory = factory; _publication = publication; _products = products.Value; _options = options.Value; _audit = audit; _logger = logger; }

    // Reject unresolved reference-wide conditions before searching hidden rows. This avoids
    // turning "empty result" versus "unknown ACL" into an object-existence probe.
    public async Task RequireReadyAsync(ClaimsPrincipal user, string sourceCode, int referenceId, CancellationToken ct)
    {
        var actor = RuntimePrincipal.Read(user);
        sourceCode = sourceCode.Trim().ToLowerInvariant();
        var key = (actor?.Sid ?? "", sourceCode, referenceId);
        if (!_referenceGates.TryGetValue(key, out var decision))
        {
            SourceSnapshot? source = null;
            try
            {
                if (actor is null) decision = StrictDecision.Deny("windows_identity_required");
                else
                {
                    source = await SourceAsync(actor, sourceCode, ct);
                    if (!source.Verified) decision = StrictDecision.Unknown(source.Reason);
                    else if (!source.References.TryGetValue(referenceId, out var reference)) decision = StrictDecision.Unknown("reference_not_in_source_profile");
                    else if (reference.UnsupportedFeatures) decision = StrictDecision.Unknown("reference_security_features_unverified");
                    else
                    {
                        var ak = (sourceCode, referenceId);
                        if (!_acl.TryGetValue(ak, out var snapshot))
                        {
                            await using var cn = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
                            _acl[ak] = snapshot = await ReadAclAsync(cn, referenceId, ct);
                        }
                        decision = StrictAccessRules.ReferenceReady(referenceId, source.Principals, snapshot,
                            VerifiedCommand(sourceCode, reference.Guid));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка предварительной проверки {Source}/{Reference}", sourceCode, referenceId);
                decision = StrictDecision.Unknown("reference_check_failed");
            }
            _audit.Decision(sourceCode, referenceId, null, source?.UserId, decision);
            _referenceGates[key] = decision;
        }
        if (!decision.Allowed) throw new ProductExplorerProblem(403, "Не удалось подтвердить право просмотра. Данные не выданы.");
    }

    public async Task<TflexAccessDecisionDto> CanReadAsync(ClaimsPrincipal user, string sourceCode,
        int referenceId, long? objectId, CancellationToken ct)
    {
        var actor = RuntimePrincipal.Read(user);
        sourceCode = sourceCode.Trim().ToLowerInvariant();
        var key = (actor?.Sid ?? "", sourceCode, referenceId, objectId ?? 0);
        if (_decisions.TryGetValue(key, out var cached)) return cached;
        SourceSnapshot? source = null;
        StrictDecision decision;
        var ruleCount = 0;
        try
        {
            if (actor is null) decision = StrictDecision.Deny("windows_identity_required");
            else if (referenceId <= 0 || objectId is null || objectId <= 0)
                decision = StrictDecision.Deny("object_required");
            else
            {
                await _publication.EnsureAppUserAsync(user, ct);
                source = await SourceAsync(actor, sourceCode, ct);
                if (!source.Verified) decision = StrictDecision.Unknown(source.Reason);
                else if (!source.References.TryGetValue(referenceId, out var reference))
                    decision = StrictDecision.Unknown("reference_not_in_source_profile");
                else
                {
                    await using var cn = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
                    var resource = await ResourceAsync(cn, reference, objectId.Value, ct);
                    if (resource is null) decision = StrictDecision.Deny("object_missing_or_ambiguous");
                    else
                    {
                        var area = resource.AccessObjectId == resource.ObjectId ? resource :
                            resource.AccessObjectId > 0 ? await ResourceAsync(cn, reference, resource.AccessObjectId, ct) : null;
                        if (area is null || area.AccessObjectId != area.ObjectId || !area.Current ||
                            area.State != 0 || area.AccessLevel != 0 || area.StageId != 0 || area.MandatoryAccess)
                            decision = StrictDecision.Unknown("access_area_not_verified", resource.AccessObjectId);
                        else
                        {
                            var ak = (sourceCode, referenceId);
                            if (!_acl.TryGetValue(ak, out var snapshot))
                            {
                                snapshot = await ReadAclAsync(cn, referenceId, ct);
                                _acl[ak] = snapshot;
                            }
                            ruleCount = snapshot.Rows.Count;
                            decision = StrictAccessRules.Evaluate(resource, source.Principals, true, snapshot, VerifiedCommand(sourceCode, reference.Guid));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { decision = StrictDecision.Deny("reporter_user_disabled_or_unmapped"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Строгая проверка не завершена: {Source}/{Reference}/{Object}", sourceCode, referenceId, objectId);
            decision = StrictDecision.Unknown("access_check_failed");
        }
        var identity = new ReporterSourceIdentityDto(sourceCode, sourceCode,
            source?.Verified == true ? "matched" : "unverified", source?.Method,
            source?.UserId, source?.UserGuid, source?.Login, source?.Name, null,
            Array.Empty<TflexUserHierarchyNodeDto>(), source?.Verified == true ? null : source?.Reason);
        var principals = source?.Principals.OrderBy(x => x).ToArray() ?? Array.Empty<long>();
        var dto = new TflexAccessDecisionDto(sourceCode, referenceId, objectId, decision.Allowed,
            "Strict", decision.Outcome.ToString(), decision.ReasonCode, identity, principals, ruleCount, 0,
            Array.Empty<TflexAccessDecisionRuleDto>(), Array.Empty<string>());
        _audit.Decision(sourceCode, referenceId, objectId, source?.UserId, decision);
        _decisions[key] = dto;
        return dto;
    }

    private async Task<SourceSnapshot> SourceAsync(RuntimeUser user, string sourceCode, CancellationToken ct)
    {
        var key = (user.Sid, sourceCode);
        if (_sources.TryGetValue(key, out var cached)) return cached;
        var profile = _products.Sources.FirstOrDefault(x => x.Key.Equals(sourceCode, StringComparison.OrdinalIgnoreCase)).Value;
        if (profile is null || string.IsNullOrWhiteSpace(profile.ExpectedServer) || string.IsNullOrWhiteSpace(profile.ExpectedDatabase))
            return Store(SourceSnapshot.Failed("source_profile_missing"));
        await using (var app = await _factory.OpenAppConnectionAsync(ct))
        await using (var cmd = Command(app, "SELECT COUNT_BIG(*) FROM app.Source WHERE Code=@code AND IsEnabled=1;"))
        {
            Add(cmd, "@code", SqlDbType.NVarChar, sourceCode, 100);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) != 1) return Store(SourceSnapshot.Failed("source_disabled_or_ambiguous"));
        }
        await using var cn = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        await using (var cmd = Command(cn, "SELECT CONVERT(nvarchar(128),SERVERPROPERTY(N'ServerName')),DB_NAME();"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct) || !string.Equals(r.GetString(0), profile.ExpectedServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(r.GetString(1), profile.ExpectedDatabase, StringComparison.OrdinalIgnoreCase))
                return Store(SourceSnapshot.Failed("source_binding_mismatch"));
        }
        var refs = new Dictionary<int, RefInfo>();
        await using (var cmd = Command(cn, @"
SELECT PK,TableName,SupportsMandatoryAccess,AuthorAccessGroupID,CONVERT(nvarchar(36),Guid)
FROM dbo.ParameterGroups WHERE [Type]=1 AND TableName IN (@card,@product,@files);"))
        {
            Add(cmd, "@card", SqlDbType.NVarChar, profile.CardTable, 128);
            Add(cmd, "@product", SqlDbType.NVarChar, profile.ProductTable, 128);
            Add(cmd, "@files", SqlDbType.NVarChar, profile.FilesTable, 128);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var rf = new RefInfo(I(r, 0), r.GetString(1), I(r, 2) != 0 || I(r, 3) != 0, r.GetString(4));
                if (!refs.TryAdd(rf.Id, rf)) return Store(SourceSnapshot.Failed("reference_metadata_ambiguous"));
            }
        }
        if (refs.Count != 3 || refs.Values.Select(x => x.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            return Store(SourceSnapshot.Failed("reference_metadata_missing"));
        var identity = await ResolveUserAsync(cn, user, ct);
        if (identity is null) return Store(SourceSnapshot.Failed("docs_identity_missing_or_ambiguous"));
        var membership = await PrincipalsAsync(cn, identity.Id, ct);
        return Store(new(membership.Ok, membership.Reason, identity.Id, identity.Guid, identity.Login,
            identity.Name, identity.Method, membership.Ids, refs));
        SourceSnapshot Store(SourceSnapshot value) { _sources[key] = value; return value; }
    }

    private async Task<UserRow?> ResolveUserAsync(SqlConnection cn, RuntimeUser user, CancellationToken ct)
    {
        var rows = await Find("LTRIM(RTRIM(p.SID)) = @value", user.Sid);
        if (rows.Count == 1) return rows[0] with { Method = "SID" };
        if (rows.Count > 1) return null;
        if (!_options.AllowDomainLoginFallback || string.IsNullOrWhiteSpace(user.Account) ||
            !_options.TrustedLoginDomains.Contains(user.Domain, StringComparer.OrdinalIgnoreCase)) return null;
        // A login with a DIFFERENT nonempty SID may never be reused as a fallback identity.
        rows = await Find("(p.[Login] = @value OR p.[Login] = @fullLogin)", user.Account);
        if (rows.Count != 1 || !string.IsNullOrWhiteSpace(rows[0].Sid)) return null;
        return rows[0] with { Method = "TrustedDomainLogin" };
        async Task<List<UserRow>> Find(string predicate, string value)
        {
            await using var cmd = Command(cn, $@"
SELECT TOP (3) u.s_ObjectID,CONVERT(nvarchar(36),u.s_Guid),u.FullName,p.[Login],p.SID
FROM dbo.Users u JOIN dbo.UserParameters p
 ON p.s_ObjectID=u.s_ObjectID AND p.s_Version=u.s_Version AND p.s_ClientViewID=u.s_ClientViewID
WHERE {ProductSql.Saved("u")} AND {ProductSql.Saved("p")} AND ({predicate})
ORDER BY u.s_ObjectID;");
            Add(cmd, "@value", SqlDbType.NVarChar, value, 255);
            Add(cmd, "@fullLogin", SqlDbType.NVarChar, user.Login, 255);
            var result = new List<UserRow>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) result.Add(new(L(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), ""));
            return result;
        }
    }

    private async Task<(bool Ok, string Reason, HashSet<long> Ids)> PrincipalsAsync(SqlConnection cn, long userId, CancellationToken ct)
    {
        var edges = new List<(long Child, long Parent)>();
        var ids = new HashSet<long>(); var frontier = new HashSet<long> { userId };
        var depth = 0;
        var max = Math.Clamp(_options.MaxPrincipalNodes, 1, 1024);
        while (frontier.Count > 0)
        {
            if (depth++ > Math.Clamp(_options.MaxHierarchyDepth, 1, 64) || ids.Count + frontier.Count > max)
                return (false, "membership_limit_exceeded", ids);
            var batch = frontier.ToArray(); frontier = new();
            var names = batch.Select((_, i) => "@p" + i).ToArray();
            await using (var cmd = Command(cn, $@"
SELECT s_ObjectID,CONVERT(nvarchar(36),s_Guid) FROM dbo.Users
WHERE s_ObjectID IN ({string.Join(",", names)}) AND {ProductSql.Saved("dbo.Users")};"))
            {
                for (var i = 0; i < batch.Length; i++) Add(cmd, names[i], SqlDbType.BigInt, batch[i]);
                var seen = new HashSet<long>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (!seen.Add(L(r, 0))) return (false, "principal_ambiguous", ids);
                    if (_options.BlockedPrincipalGuids.Contains(S(r, 1) ?? "", StringComparer.OrdinalIgnoreCase))
                        return (false, "blocked_principal", ids);
                }
                if (seen.Count != batch.Length) return (false, "principal_not_current", ids);
            }
            foreach (var id in batch) ids.Add(id);
            await using (var cmd = Command(cn, $@"
SELECT TOP (@limit) s_ObjectID,s_ParentID,BeginDate,EndDate
FROM dbo.UsersHierarchy h WHERE h.s_ObjectID IN ({string.Join(",", names)}) AND {ProductSql.Saved("h")};"))
            {
                for (var i = 0; i < batch.Length; i++) Add(cmd, names[i], SqlDbType.BigInt, batch[i]);
                Add(cmd, "@limit", SqlDbType.Int, max * 8 + 1);
                var count = 0;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (++count > max * 8) return (false, "membership_rows_incomplete", ids);
                    // Equal boundary timestamps occur in the supplied structural snapshot.
                    // A real interval is not guessed; requires separate verification.
                    if (r.IsDBNull(2) != r.IsDBNull(3) || (!r.IsDBNull(2) && r.GetDateTime(2) != r.GetDateTime(3)))
                        return (false, "membership_period_unverified", ids);
                    var parent = L(r, 1);
                    if (parent < 0) return (false, "membership_parent_unverified", ids);
                    edges.Add((L(r, 0), parent));
                    if (parent == L(r, 0)) return (false, "membership_cycle", ids);
                    if (parent > 0 && !ids.Contains(parent)) frontier.Add(parent);
                }
            }
        }
        if (StrictAccessRules.HasCycle(edges)) return (false, "membership_cycle", ids);
        return (true, "matched", ids);
    }

    private async Task<StrictResource?> ResourceAsync(SqlConnection cn, RefInfo reference, long id, CancellationToken ct)
    {
        // No fallback to s_ParentID: a physical parent is not proof of an ACL area.
        var columns = await SqlHelpers.GetColumnsAsync(cn, "dbo", reference.Table, ct);
        var required = new[] { "s_ObjectID", "s_ActualVersion", "s_ClientViewID", "s_Deleted", "s_State", "s_AccessObjectID", "s_AccessLevel" };
        if (required.Any(x => !columns.Contains(x))) return null;
        var stage = columns.Contains("s_StageID") ? "s_StageID" : "CAST(0 AS int)";
        await using var cmd = Command(cn, $@"
SELECT TOP (2) s_AccessObjectID,s_State,s_AccessLevel,{stage}
FROM dbo.{ProductSql.Q(reference.Table)} WHERE s_ObjectID=@id
 AND s_ActualVersion=1 AND s_ClientViewID=0 AND s_Deleted=0;");
        Add(cmd, "@id", SqlDbType.BigInt, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var result = new StrictResource(reference.Id, id, L(r, 0), true, I(r, 1), I(r, 2), L(r, 3), reference.UnsupportedFeatures);
        return await r.ReadAsync(ct) ? null : result;
    }

    private async Task<StrictAclSnapshot> ReadAclAsync(SqlConnection cn, int reference, CancellationToken ct)
    {
        var cap = Math.Clamp(_options.MaxAclRows, 1, 50000);
        var rows = new List<StrictAclRow>();
        await using (var cmd = Command(cn, @"
SELECT TOP (@cap) AccessTypeID,AccessGroupID,UserID,ReferenceID,ObjectID,StageID,LinkTypeID,LinkID,
 AccessDirection,xAuthorID,xReferenceID,xObjectID,xAccessObjectID,xReferenceAuthorGroupID
FROM dbo.xAccessRights WHERE ReferenceID IN (0,@ref) OR xReferenceID=@ref;"))
        {
            Add(cmd, "@cap", SqlDbType.Int, cap + 1); Add(cmd, "@ref", SqlDbType.Int, reference);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new(I(r, 0), I(r, 1), L(r, 2), I(r, 3), L(r, 4), I(r, 5), I(r, 6), L(r, 7), I(r, 8), I(r, 9), I(r, 10), L(r, 11), L(r, 12), I(r, 13)));
        }
        if (rows.Count > cap) return new(false, rows, new Dictionary<int, StrictAccessGroup>());
        var groups = new Dictionary<int, StrictAccessGroup>();
        var groupIds = rows.Select(x => x.AccessGroupId).Where(x => x > 0).Distinct().ToArray();
        if (groupIds.Length > 1024) return new(false, rows, groups);
        if (groupIds.Length == 0) return new(true, rows, groups);
        var keys = groupIds.Select((_, i) => "@g" + i).ToArray();
        await using var cmd2 = Command(cn, $@"
SELECT g.PK,g.TypeID,c.CommandID,c.Enable FROM dbo.AccessGroups g
LEFT JOIN dbo.AccessGroupCommands c ON c.GroupID=g.PK WHERE g.PK IN ({string.Join(",", keys)});");
        for (var i = 0; i < groupIds.Length; i++) Add(cmd2, keys[i], SqlDbType.Int, groupIds[i]);
        var types = new Dictionary<int, int>(); var commands = new Dictionary<int, Dictionary<int, bool?>>();
        await using var rr = await cmd2.ExecuteReaderAsync(ct);
        var readCount = 0;
        while (await rr.ReadAsync(ct))
        {
            if (++readCount > cap * 10) return new(false, rows, groups);
            var id = I(rr, 0); var type = I(rr, 1);
            if (types.TryGetValue(id, out var priorType) && priorType != type) return new(false, rows, groups);
            types[id] = type;
            if (!commands.TryGetValue(id, out var map)) commands[id] = map = new();
            if (rr.IsDBNull(2)) continue;
            var command = I(rr, 2); var enabled = rr.IsDBNull(3) ? -1 : I(rr, 3);
            if (!map.TryAdd(command, enabled == 1 ? true : enabled == 0 ? false : null))
                return new(false, rows, groups);
        }
        foreach (var id in types.Keys) groups[id] = new(types[id], commands[id]);
        return new(true, rows, groups);
    }

    private int? VerifiedCommand(string source, string referenceGuid)
    {
        var sourceProfile = _options.VerifiedObjectReadCommands.FirstOrDefault(x => x.Key.Equals(source, StringComparison.OrdinalIgnoreCase)).Value;
        if (sourceProfile is null) return null;
        var command = sourceProfile.FirstOrDefault(x => x.Key.Equals(referenceGuid, StringComparison.OrdinalIgnoreCase)).Value;
        return command > 0 ? command : null;
    }

    private SqlCommand Command(SqlConnection cn, string sql)
    { var cmd = cn.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = Math.Clamp(_options.SqlTimeoutSeconds, 1, 60); return cmd; }
    private static void Add(SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    { var p = size > 0 ? new SqlParameter(name, type, size) : new SqlParameter(name, type); p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p); }
    private static int I(SqlDataReader r, int i) => r.IsDBNull(i) ? throw new InvalidDataException("Null ACL field") : Convert.ToInt32(r.GetValue(i));
    private static long L(SqlDataReader r, int i) => r.IsDBNull(i) ? throw new InvalidDataException("Null ACL field") : Convert.ToInt64(r.GetValue(i));
    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private sealed record RefInfo(int Id, string Table, bool UnsupportedFeatures, string Guid);
    private sealed record UserRow(long Id, string? Guid, string? Name, string? Login, string? Sid, string Method);
    private sealed record SourceSnapshot(bool Verified, string Reason, long? UserId, string? UserGuid, string? Login,
        string? Name, string? Method, HashSet<long> Principals, Dictionary<int, RefInfo> References)
    {
        public static SourceSnapshot Failed(string reason) => new(false, reason, null, null, null, null, null, new(), new());
    }
}
