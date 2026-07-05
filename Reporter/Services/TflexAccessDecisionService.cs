using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface ITflexAccessDecisionService
{
    bool ShouldEnforceAppPolicy { get; }
    bool ShouldEnforceTflexRead { get; }

    Task<TflexAccessDecisionDto> CanReadAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int referenceId,
        long? objectId,
        CancellationToken ct);
}

public sealed class TflexAccessDecisionService : ITflexAccessDecisionService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterIdentityService _identity;
    private readonly ReporterOptions _options;

    public TflexAccessDecisionService(
        IReporterSqlConnectionFactory factory,
        IReporterIdentityService identity,
        IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _identity = identity;
        _options = options.Value;
    }

    public bool ShouldEnforceAppPolicy => GetMode() is SecurityMode.AppPolicy or SecurityMode.TflexRead;

    public bool ShouldEnforceTflexRead => GetMode() is SecurityMode.TflexRead;

    public async Task<TflexAccessDecisionDto> CanReadAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int referenceId,
        long? objectId,
        CancellationToken ct)
    {
        var mode = GetMode();
        var modeName = GetModeName(mode);
        var identity = await _identity.ResolveSourceAsync(user, sourceCode, ct);
        var principalIds = GetPrincipalIds(identity);
        var warnings = new List<string>();

        if (mode is SecurityMode.Disabled)
        {
            return SimpleDecision(
                sourceCode,
                referenceId,
                objectId,
                allowed: true,
                modeName,
                "disabled",
                "SecurityMode=Disabled: проверка доступа выключена.",
                identity,
                principalIds,
                warnings);
        }

        if (mode is SecurityMode.Preview or SecurityMode.AppPolicy)
        {
            warnings.Add(mode is SecurityMode.Preview
                ? "SecurityMode=Preview: xAccessRights рассчитывается только для диагностики и не фильтрует выдачу."
                : "SecurityMode=AppPolicy: применяются только политики репортера; xAccessRights не фильтрует выдачу.");

            return SimpleDecision(
                sourceCode,
                referenceId,
                objectId,
                allowed: true,
                modeName,
                mode is SecurityMode.Preview ? "preview" : "app_policy_only",
                mode is SecurityMode.Preview
                    ? "T-FLEX ACL показан только для аудита."
                    : "T-FLEX ACL не включен в режим применения.",
                identity,
                principalIds,
                warnings);
        }

        if (!identity.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)
            || identity.DocsUserObjectId is null)
        {
            warnings.Add("Пользователь не сопоставлен с T-FLEX Users/UserParameters в выбранном source.");
            return SimpleDecision(
                sourceCode,
                referenceId,
                objectId,
                allowed: false,
                modeName,
                "identity_not_matched",
                "Нет source-scoped соответствия Windows пользователя пользователю T-FLEX.",
                identity,
                principalIds,
                warnings);
        }

        await using var cn = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        if (!await SqlHelpers.TableExistsAsync(cn, "dbo", "xAccessRights", ct))
        {
            warnings.Add("Таблица dbo.xAccessRights не найдена.");
            return SimpleDecision(
                sourceCode,
                referenceId,
                objectId,
                allowed: false,
                modeName,
                "xaccess_missing",
                "Не удалось проверить права T-FLEX: dbo.xAccessRights отсутствует.",
                identity,
                principalIds,
                warnings);
        }

        var accessGroupKey = await FindAccessGroupKeyColumnAsync(cn, ct);
        if (accessGroupKey is null)
            warnings.Add("Не удалось определить числовой ключ AccessGroups; используются только AccessGroupID и команды.");

        warnings.Add("Stage 5B применяет source-scoped права чтения по xAccessRights. Наследование от родительских папок и права этапов процесса пока не разворачиваются как полный ACL T-FLEX.");

        var rawRows = await ReadRightsAsync(cn, referenceId, objectId, accessGroupKey, ct);
        if (rawRows.Count >= Math.Clamp(_options.SecurityMaxRightsRows, 1, 5000))
            warnings.Add("Список xAccessRights мог быть усечен настройкой SecurityMaxRightsRows.");

        var commandMap = await ReadCommandsAsync(
            cn,
            rawRows
                .Select(row => row.AccessGroupId)
                .Where(groupId => groupId > 0)
                .Distinct()
                .ToArray(),
            ct);

        var rules = rawRows
            .Select(row => ToRule(row, principalIds, commandMap))
            .OrderByDescending(rule => rule.AppliesToCurrentPrincipal)
            .ThenByDescending(rule => rule.Specificity)
            .ThenBy(rule => rule.Effect == "deny_read" ? 0 : rule.Effect == "allow_read" ? 1 : 2)
            .ToArray();

        var applicable = rules
            .Where(rule => rule.AppliesToCurrentPrincipal)
            .ToArray();

        var selected = SelectEffectiveRule(applicable, objectId, referenceId);
        if (selected is not null)
        {
            return DecisionFromRule(
                sourceCode,
                referenceId,
                objectId,
                modeName,
                selected,
                identity,
                principalIds,
                rules,
                warnings);
        }

        var noRuleAllowed = !_options.TflexDenyWhenNoReadRule;
        return FullDecision(
            sourceCode,
            referenceId,
            objectId,
            noRuleAllowed,
            modeName,
            noRuleAllowed ? "no_read_rule_allowed_by_option" : "no_read_rule",
            noRuleAllowed
                ? "Применимых строк чтения не найдено, но TflexDenyWhenNoReadRule=false."
                : "Не найдена применимая строка xAccessRights с правом чтения/просмотра.",
            identity,
            principalIds,
            rules,
            warnings);
    }

    private static IReadOnlyList<long> GetPrincipalIds(ReporterSourceIdentityDto identity) =>
        identity.Hierarchy
            .Select(node => node.ObjectId)
            .Append(identity.DocsUserObjectId ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    private async Task<IReadOnlyList<RawRightRow>> ReadRightsAsync(
        SqlConnection cn,
        int referenceId,
        long? objectId,
        string? accessGroupKey,
        CancellationToken ct)
    {
        var maxRows = Math.Clamp(_options.SecurityMaxRightsRows, 1, 5000);
        var hasAccessGroups =
            accessGroupKey is not null
            && await SqlHelpers.TableExistsAsync(cn, "dbo", "AccessGroups", ct);

        var accessGroupJoin = hasAccessGroups
            ? $"LEFT JOIN dbo.AccessGroups ag ON TRY_CONVERT(int, ag.{SqlHelpers.QuoteName(accessGroupKey!)}) = TRY_CONVERT(int, ar.AccessGroupID)"
            : string.Empty;

        var accessGroupName = hasAccessGroups
            ? "TRY_CONVERT(nvarchar(255), ag.Name)"
            : "CAST(NULL AS nvarchar(255))";

        var accessGroupType = hasAccessGroups
            ? "TRY_CONVERT(int, ag.TypeID)"
            : "CAST(NULL AS int)";

        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT TOP (@maxRows)
    TRY_CONVERT(int, ar.AccessTypeID) AS AccessTypeId,
    TRY_CONVERT(int, ar.AccessGroupID) AS AccessGroupId,
    {accessGroupName} AS AccessGroupName,
    {accessGroupType} AS AccessGroupTypeId,
    TRY_CONVERT(bigint, ar.UserID) AS UserId,
    TRY_CONVERT(int, ar.ReferenceID) AS ReferenceId,
    TRY_CONVERT(bigint, ar.ObjectID) AS ObjectId,
    TRY_CONVERT(int, ar.StageID) AS StageId,
    TRY_CONVERT(int, ar.LinkTypeID) AS LinkTypeId,
    TRY_CONVERT(bigint, ar.LinkID) AS LinkId,
    TRY_CONVERT(int, ar.AccessDirection) AS AccessDirection,
    TRY_CONVERT(int, ar.xReferenceID) AS XReferenceId,
    TRY_CONVERT(bigint, ar.xObjectID) AS XObjectId
FROM dbo.xAccessRights ar
{accessGroupJoin}
WHERE (
        TRY_CONVERT(int, ar.ReferenceID) = @referenceId
        OR TRY_CONVERT(int, ar.xReferenceID) = @referenceId
      )
  AND (
        @objectId IS NULL
        OR TRY_CONVERT(bigint, ar.ObjectID) IN (0, @objectId)
        OR TRY_CONVERT(bigint, ar.xObjectID) IN (0, @objectId)
      )
ORDER BY
    CASE
        WHEN TRY_CONVERT(bigint, ar.ObjectID) = @objectId
          OR TRY_CONVERT(bigint, ar.xObjectID) = @objectId
            THEN 0
        ELSE 1
    END,
    CASE
        WHEN TRY_CONVERT(bigint, ar.UserID) <> 0
            THEN 0
        ELSE 1
    END,
    TRY_CONVERT(int, ar.AccessTypeID),
    TRY_CONVERT(int, ar.AccessGroupID);";

        cmd.Parameters.Add(new SqlParameter("@maxRows", SqlDbType.Int) { Value = maxRows });
        cmd.Parameters.Add(new SqlParameter("@referenceId", SqlDbType.Int) { Value = referenceId });
        cmd.Parameters.Add(new SqlParameter("@objectId", SqlDbType.BigInt) { Value = (object?)objectId ?? DBNull.Value });

        var result = new List<RawRightRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RawRightRow(
                ReadInt(reader, "AccessTypeId"),
                ReadInt(reader, "AccessGroupId"),
                reader.GetNullableString("AccessGroupName"),
                ReadNullableInt(reader, "AccessGroupTypeId"),
                ReadLong(reader, "UserId"),
                ReadInt(reader, "ReferenceId"),
                ReadLong(reader, "ObjectId"),
                ReadInt(reader, "StageId"),
                ReadInt(reader, "LinkTypeId"),
                ReadLong(reader, "LinkId"),
                ReadInt(reader, "AccessDirection"),
                ReadInt(reader, "XReferenceId"),
                ReadLong(reader, "XObjectId")));
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<int, IReadOnlyList<AccessGroupCommandDto>>> ReadCommandsAsync(
        SqlConnection cn,
        IReadOnlyList<int> groupIds,
        CancellationToken ct)
    {
        if (groupIds.Count == 0
            || !await SqlHelpers.TableExistsAsync(cn, "dbo", "AccessGroupCommands", ct))
        {
            return new Dictionary<int, IReadOnlyList<AccessGroupCommandDto>>();
        }

        await using var cmd = cn.CreateCommand();
        var parameterNames = new List<string>();
        for (var i = 0; i < groupIds.Count; i++)
        {
            var name = $"@group{i}";
            parameterNames.Add(name);
            cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Int) { Value = groupIds[i] });
        }

        cmd.CommandText = $@"
SELECT
    TRY_CONVERT(int, GroupID) AS GroupId,
    TRY_CONVERT(int, CommandID) AS CommandId,
    TRY_CONVERT(bit, Enable) AS Enabled
FROM dbo.AccessGroupCommands
WHERE TRY_CONVERT(int, GroupID) IN ({string.Join(",", parameterNames)})
ORDER BY GroupID, CommandID;";

        var temp = new Dictionary<int, List<AccessGroupCommandDto>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var groupId = ReadInt(reader, "GroupId");
            if (!temp.TryGetValue(groupId, out var list))
            {
                list = new List<AccessGroupCommandDto>();
                temp[groupId] = list;
            }

            var enabledOrdinal = reader.GetOrdinal("Enabled");
            list.Add(new AccessGroupCommandDto(
                ReadInt(reader, "CommandId"),
                !reader.IsDBNull(enabledOrdinal) && Convert.ToBoolean(reader.GetValue(enabledOrdinal))));
        }

        return temp.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AccessGroupCommandDto>)pair.Value);
    }

    private TflexAccessDecisionRuleDto ToRule(
        RawRightRow row,
        IReadOnlyCollection<long> principalIds,
        IReadOnlyDictionary<int, IReadOnlyList<AccessGroupCommandDto>> commandMap)
    {
        var referenceId = row.ReferenceId != 0 ? row.ReferenceId : row.XReferenceId;
        var objectId = row.ObjectId != 0 ? row.ObjectId : row.XObjectId;
        var applies = row.UserId == 0 || principalIds.Contains(row.UserId);
        var scope = GetScope(row, referenceId, objectId);
        var readCommand = GetReadCommandState(row.AccessGroupId, commandMap);
        var effect = GetEffect(row.AccessGroupName, readCommand);
        var specificity = GetSpecificity(row, referenceId, objectId);

        return new TflexAccessDecisionRuleDto(
            row.AccessTypeId,
            row.AccessGroupId,
            row.AccessGroupName,
            row.UserId,
            applies,
            referenceId,
            objectId,
            scope,
            effect,
            readCommand,
            specificity,
            GetRuleReason(row.AccessGroupName, readCommand, effect));
    }

    private bool? GetReadCommandState(
        int accessGroupId,
        IReadOnlyDictionary<int, IReadOnlyList<AccessGroupCommandDto>> commandMap)
    {
        if (!commandMap.TryGetValue(accessGroupId, out var commands))
            return null;

        var command = commands.FirstOrDefault(item => item.CommandId == _options.TflexReadCommandId);
        return command is null ? null : command.Enabled;
    }

    private static string GetEffect(string? accessGroupName, bool? readCommandEnabled)
    {
        if (readCommandEnabled is false)
            return "deny_read";

        if (readCommandEnabled is true)
            return "allow_read";

        if (IsHardDenyGroup(accessGroupName))
            return "deny_read";

        if (IsReadGroup(accessGroupName))
            return "allow_read";

        return "neutral";
    }

    private static string? GetRuleReason(string? accessGroupName, bool? readCommandEnabled, string effect)
    {
        if (readCommandEnabled is false)
            return "Команда чтения/open отключена в AccessGroupCommands.";

        if (readCommandEnabled is true)
            return "Команда чтения/open включена в AccessGroupCommands.";

        if (effect == "deny_read")
            return $"AccessGroup '{accessGroupName}' распознан как запрет чтения.";

        if (effect == "allow_read")
            return $"AccessGroup '{accessGroupName}' распознан как уровень просмотра.";

        return string.IsNullOrWhiteSpace(accessGroupName)
            ? "Нет имени AccessGroup и команды чтения для классификации."
            : $"AccessGroup '{accessGroupName}' не классифицирован как чтение или запрет чтения.";
    }

    private static string GetScope(RawRightRow row, int referenceId, long objectId)
    {
        if (objectId != 0)
            return "object";

        if (row.StageId != 0)
            return "stage";

        if (row.LinkId != 0 || row.LinkTypeId != 0)
            return "link";

        if (referenceId != 0)
            return "reference";

        return "global";
    }

    private static int GetSpecificity(RawRightRow row, int referenceId, long objectId)
    {
        var score = 0;
        if (objectId != 0)
            score += 100;
        if (referenceId != 0)
            score += 10;
        if (row.UserId != 0)
            score += 5;
        if (row.StageId != 0 || row.LinkId != 0 || row.LinkTypeId != 0)
            score += 1;
        return score;
    }

    private static TflexAccessDecisionRuleDto? SelectEffectiveRule(
        IReadOnlyList<TflexAccessDecisionRuleDto> applicable,
        long? objectId,
        int referenceId)
    {
        var candidates = applicable
            .Where(rule => rule.Effect is "allow_read" or "deny_read")
            .Where(rule => rule.ReferenceId == referenceId || rule.ReferenceId == 0)
            .Where(rule => objectId is null || rule.ObjectId == 0 || rule.ObjectId == objectId.Value)
            .OrderByDescending(rule => rule.ObjectId != 0 && objectId is not null && rule.ObjectId == objectId.Value)
            .ThenByDescending(rule => rule.UserId != 0)
            .ThenBy(rule => rule.Effect == "deny_read" ? 0 : 1)
            .ThenByDescending(rule => rule.Specificity)
            .ToArray();

        return candidates.FirstOrDefault();
    }

    private static TflexAccessDecisionDto DecisionFromRule(
        string sourceCode,
        int referenceId,
        long? objectId,
        string modeName,
        TflexAccessDecisionRuleDto rule,
        ReporterSourceIdentityDto identity,
        IReadOnlyList<long> principalIds,
        IReadOnlyList<TflexAccessDecisionRuleDto> rules,
        IReadOnlyList<string> warnings)
    {
        var allowed = rule.Effect.Equals("allow_read", StringComparison.OrdinalIgnoreCase);
        var status = allowed ? "allowed_by_xaccess" : "denied_by_xaccess";
        var accessGroup = string.IsNullOrWhiteSpace(rule.AccessGroupName)
            ? $"AccessGroupID={rule.AccessGroupId}"
            : $"{rule.AccessGroupName} ({rule.AccessGroupId})";
        var principal = rule.UserId == 0 ? "общая строка UserID=0" : $"UserID={rule.UserId}";
        var reason = $"{(allowed ? "Разрешено" : "Запрещено")}: {accessGroup}, {principal}, scope={rule.Scope}. {rule.Reason}";

        return FullDecision(
            sourceCode,
            referenceId,
            objectId,
            allowed,
            modeName,
            status,
            reason,
            identity,
            principalIds,
            rules,
            warnings);
    }

    private static TflexAccessDecisionDto SimpleDecision(
        string sourceCode,
        int referenceId,
        long? objectId,
        bool allowed,
        string mode,
        string status,
        string reason,
        ReporterSourceIdentityDto identity,
        IReadOnlyList<long> principals,
        IReadOnlyList<string> warnings) =>
        new(
            sourceCode,
            referenceId,
            objectId,
            allowed,
            mode,
            status,
            reason,
            identity,
            principals,
            0,
            0,
            Array.Empty<TflexAccessDecisionRuleDto>(),
            warnings);

    private static TflexAccessDecisionDto FullDecision(
        string sourceCode,
        int referenceId,
        long? objectId,
        bool allowed,
        string mode,
        string status,
        string reason,
        ReporterSourceIdentityDto identity,
        IReadOnlyList<long> principals,
        IReadOnlyList<TflexAccessDecisionRuleDto> rules,
        IReadOnlyList<string> warnings) =>
        new(
            sourceCode,
            referenceId,
            objectId,
            allowed,
            mode,
            status,
            reason,
            identity,
            principals,
            rules.Count,
            rules.Count(rule => rule.AppliesToCurrentPrincipal),
            rules,
            warnings);

    private SecurityMode GetMode()
    {
        var mode = (_options.SecurityMode ?? string.Empty).Trim();
        if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return SecurityMode.Disabled;
        if (mode.Equals("AppPolicy", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("ReporterOnly", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("EnforceAppPolicy", StringComparison.OrdinalIgnoreCase))
            return SecurityMode.AppPolicy;
        if (mode.Equals("TflexRead", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("EnforceTflexRead", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Strict", StringComparison.OrdinalIgnoreCase))
            return SecurityMode.TflexRead;

        return SecurityMode.Preview;
    }

    private static string GetModeName(SecurityMode mode) => mode switch
    {
        SecurityMode.Disabled => "Disabled",
        SecurityMode.AppPolicy => "AppPolicy",
        SecurityMode.TflexRead => "TflexRead",
        _ => "Preview"
    };

    private static bool IsHardDenyGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("запрещено всё", StringComparison.OrdinalIgnoreCase)
            || name.Contains("запрещено все", StringComparison.OrdinalIgnoreCase)
            || name.Contains("без доступа", StringComparison.OrdinalIgnoreCase)
            || name.Contains("deny", StringComparison.OrdinalIgnoreCase)
            || name.Contains("forbid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("просмотр", StringComparison.OrdinalIgnoreCase)
            || name.Contains("полный", StringComparison.OrdinalIgnoreCase)
            || name.Contains("без огранич", StringComparison.OrdinalIgnoreCase)
            || name.Contains("редактор", StringComparison.OrdinalIgnoreCase)
            || name.Contains("административ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("исправ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("подпись", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> FindAccessGroupKeyColumnAsync(
        SqlConnection cn,
        CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(cn, "dbo", "AccessGroups", ct))
            return null;

        var columns = await SqlHelpers.GetColumnsAsync(cn, "dbo", "AccessGroups", ct);
        foreach (var candidate in new[] { "AccessGroupID", "ID", "PK", "s_PK", "s_ObjectID" })
        {
            if (columns.Contains(candidate))
                return candidate;
        }

        await using (var identityCommand = cn.CreateCommand())
        {
            identityCommand.CommandText = @"
SELECT TOP (1) name
FROM sys.columns
WHERE object_id = OBJECT_ID(N'dbo.AccessGroups')
  AND is_identity = 1
ORDER BY column_id;";

            var identity = Convert.ToString(await identityCommand.ExecuteScalarAsync(ct));
            if (!string.IsNullOrWhiteSpace(identity))
                return identity;
        }

        await using var primaryKeyCommand = cn.CreateCommand();
        primaryKeyCommand.CommandText = @"
SELECT TOP (1) c.name
FROM sys.indexes i
JOIN sys.index_columns ic
  ON ic.object_id = i.object_id
 AND ic.index_id = i.index_id
JOIN sys.columns c
  ON c.object_id = ic.object_id
 AND c.column_id = ic.column_id
JOIN sys.types t
  ON t.user_type_id = c.user_type_id
WHERE i.object_id = OBJECT_ID(N'dbo.AccessGroups')
  AND i.is_primary_key = 1
  AND t.name IN (N'tinyint', N'smallint', N'int', N'bigint', N'numeric', N'decimal')
ORDER BY ic.key_ordinal;";

        var primaryKey = Convert.ToString(await primaryKeyCommand.ExecuteScalarAsync(ct));
        return string.IsNullOrWhiteSpace(primaryKey) ? null : primaryKey;
    }

    private static int ReadInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static int? ReadNullableInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static long ReadLong(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal));
    }

    private enum SecurityMode
    {
        Disabled,
        Preview,
        AppPolicy,
        TflexRead
    }

    private sealed record RawRightRow(
        int AccessTypeId,
        int AccessGroupId,
        string? AccessGroupName,
        int? AccessGroupTypeId,
        long UserId,
        int ReferenceId,
        long ObjectId,
        int StageId,
        int LinkTypeId,
        long LinkId,
        int AccessDirection,
        int XReferenceId,
        long XObjectId);
}
