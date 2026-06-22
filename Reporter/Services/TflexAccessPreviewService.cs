using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface ITflexAccessPreviewService
{
    Task<TflexAccessPreviewDto> PreviewAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int referenceId,
        long? objectId,
        CancellationToken ct);
}

public sealed class TflexAccessPreviewService : ITflexAccessPreviewService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterIdentityService _identity;
    private readonly IReporterAccessService _access;
    private readonly ReporterOptions _options;

    public TflexAccessPreviewService(
        IReporterSqlConnectionFactory factory,
        IReporterIdentityService identity,
        IReporterAccessService access,
        IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _identity = identity;
        _access = access;
        _options = options.Value;
    }

    public async Task<TflexAccessPreviewDto> PreviewAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int referenceId,
        long? objectId,
        CancellationToken ct)
    {
        var identity = await _identity.ResolveSourceAsync(
            user,
            sourceCode,
            ct);

        var principals = identity.Hierarchy
            .Select(node => node.ObjectId)
            .Append(identity.DocsUserObjectId ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();

        var groupAccess = await _access.GetGroupAccessAsync(
            user,
            sourceCode,
            referenceId,
            ct);

        var allowedRelations = await _access.GetAllowedRelationsAsync(
            user,
            sourceCode,
            referenceId,
            ct);

        var appPolicy = groupAccess is null
            ? new ReporterAppPolicyPreviewDto(
                false,
                false,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                0)
            : new ReporterAppPolicyPreviewDto(
                true,
                groupAccess.CanSeeInMenu,
                groupAccess.CanSearch,
                groupAccess.CanOpenCard,
                groupAccess.CanExport,
                groupAccess.MaxObjectDepth,
                groupAccess.MaxFileTreeDepth,
                groupAccess.MaxRowsPerPage,
                groupAccess.MaxRelatedObjects,
                allowedRelations.Count);

        await using var cn = await _factory.OpenSourceConnectionAsync(
            sourceCode,
            ct);

        var reference = await ReadReferenceAsync(
            cn,
            referenceId,
            ct);

        var warnings = new List<string>
        {
            "Режим preview: xAccessRights пока не фильтрует выдачу репортера.",
            "AccessTypeID и CommandID показаны как исходные значения; итоговый allow/deny не вычисляется до проверки доменной семантики T-FLEX."
        };

        if (!identity.Status.Equals(
                "matched",
                StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                "Пользователь T-FLEX не сопоставлен; применимость персональных и групповых строк неполна.");
        }

        if (!await SqlHelpers.TableExistsAsync(
                cn,
                "dbo",
                "xAccessRights",
                ct))
        {
            warnings.Add("Таблица dbo.xAccessRights не найдена.");

            return new TflexAccessPreviewDto(
                sourceCode,
                identity,
                reference,
                appPolicy,
                principals.OrderBy(id => id).ToArray(),
                0,
                0,
                Array.Empty<TflexAccessRightRowDto>(),
                warnings);
        }

        var accessGroupKey = await FindAccessGroupKeyColumnAsync(cn, ct);

        var rawRows = await ReadRightsAsync(
            cn,
            referenceId,
            objectId,
            accessGroupKey,
            ct);

        var configuredMaxRows = Math.Clamp(
            _options.SecurityMaxRightsRows,
            1,
            5000);

        if (rawRows.Count >= configuredMaxRows)
        {
            warnings.Add(
                $"Показаны первые {configuredMaxRows} строк; результат мог быть усечен.");
        }

        var commandMap = await ReadCommandsAsync(
            cn,
            rawRows
                .Select(row => row.AccessGroupId)
                .Where(id => id > 0)
                .Distinct()
                .ToArray(),
            ct);

        var rows = rawRows
            .Select(row => new TflexAccessRightRowDto(
                row.AccessTypeId,
                row.AccessGroupId,
                row.AccessGroupName,
                row.AccessGroupTypeId,
                row.UserId,
                row.UserId == 0 || principals.Contains(row.UserId),
                row.ReferenceId,
                row.ObjectId,
                row.StageId,
                row.LinkTypeId,
                row.LinkId,
                row.AccessDirection,
                row.XAuthorId,
                row.XReferenceId,
                row.XObjectId,
                row.XAccessObjectId,
                row.XReferenceAuthorGroupId,
                GetScope(row),
                commandMap.TryGetValue(
                    row.AccessGroupId,
                    out var commands)
                    ? commands
                    : Array.Empty<AccessGroupCommandDto>()))
            .ToArray();

        return new TflexAccessPreviewDto(
            sourceCode,
            identity,
            reference,
            appPolicy,
            principals.OrderBy(id => id).ToArray(),
            rows.Length,
            rows.Count(row => row.AppliesToCurrentPrincipal),
            rows,
            warnings);
    }

    private async Task<ReporterReferenceDto> ReadReferenceAsync(
        SqlConnection cn,
        int referenceId,
        CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(
                cn,
                "dbo",
                "ParameterGroups",
                ct))
        {
            return new ReporterReferenceDto(
                referenceId,
                null,
                null);
        }

        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT TOP (1)
    TRY_CONVERT(int, PK) AS ReferenceId,
    TRY_CONVERT(nvarchar(255), TableName) AS TableName,
    TRY_CONVERT(nvarchar(255), Caption) AS Caption
FROM dbo.ParameterGroups
WHERE TRY_CONVERT(int, PK) = @referenceId;";

        cmd.Parameters.Add(new SqlParameter(
            "@referenceId",
            SqlDbType.Int)
        {
            Value = referenceId
        });

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return new ReporterReferenceDto(
                referenceId,
                null,
                null);
        }

        return new ReporterReferenceDto(
            reader.GetInt32Flexible("ReferenceId"),
            reader.GetNullableString("TableName"),
            reader.GetNullableString("Caption"));
    }

    private async Task<IReadOnlyList<RawRightRow>> ReadRightsAsync(
        SqlConnection cn,
        int referenceId,
        long? objectId,
        string? accessGroupKey,
        CancellationToken ct)
    {
        var maxRows = Math.Clamp(
            _options.SecurityMaxRightsRows,
            1,
            5000);

        var hasAccessGroups =
            accessGroupKey is not null
            && await SqlHelpers.TableExistsAsync(
                cn,
                "dbo",
                "AccessGroups",
                ct);

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
    TRY_CONVERT(int, ar.xAuthorID) AS XAuthorId,
    TRY_CONVERT(int, ar.xReferenceID) AS XReferenceId,
    TRY_CONVERT(bigint, ar.xObjectID) AS XObjectId,
    TRY_CONVERT(bigint, ar.xAccessObjectID) AS XAccessObjectId,
    TRY_CONVERT(int, ar.xReferenceAuthorGroupID)
        AS XReferenceAuthorGroupId
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

        cmd.Parameters.Add(new SqlParameter(
            "@maxRows",
            SqlDbType.Int)
        {
            Value = maxRows
        });

        cmd.Parameters.Add(new SqlParameter(
            "@referenceId",
            SqlDbType.Int)
        {
            Value = referenceId
        });

        cmd.Parameters.Add(new SqlParameter(
            "@objectId",
            SqlDbType.BigInt)
        {
            Value = (object?)objectId ?? DBNull.Value
        });

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
                ReadInt(reader, "XAuthorId"),
                ReadInt(reader, "XReferenceId"),
                ReadLong(reader, "XObjectId"),
                ReadLong(reader, "XAccessObjectId"),
                ReadInt(reader, "XReferenceAuthorGroupId")));
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<
        int,
        IReadOnlyList<AccessGroupCommandDto>>> ReadCommandsAsync(
        SqlConnection cn,
        IReadOnlyList<int> groupIds,
        CancellationToken ct)
    {
        if (groupIds.Count == 0
            || !await SqlHelpers.TableExistsAsync(
                cn,
                "dbo",
                "AccessGroupCommands",
                ct))
        {
            return new Dictionary<
                int,
                IReadOnlyList<AccessGroupCommandDto>>();
        }

        await using var cmd = cn.CreateCommand();
        var parameterNames = new List<string>();

        for (var i = 0; i < groupIds.Count; i++)
        {
            var name = $"@group{i}";
            parameterNames.Add(name);
            cmd.Parameters.Add(new SqlParameter(
                name,
                SqlDbType.Int)
            {
                Value = groupIds[i]
            });
        }

        cmd.CommandText = $@"
SELECT
    TRY_CONVERT(int, GroupID) AS GroupId,
    TRY_CONVERT(int, CommandID) AS CommandId,
    TRY_CONVERT(bit, Enable) AS Enabled
FROM dbo.AccessGroupCommands
WHERE TRY_CONVERT(int, GroupID)
      IN ({string.Join(",", parameterNames)})
ORDER BY GroupID, CommandID;";

        var temp = new Dictionary<
            int,
            List<AccessGroupCommandDto>>();

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
                !reader.IsDBNull(enabledOrdinal)
                && Convert.ToBoolean(
                    reader.GetValue(enabledOrdinal))));
        }

        return temp.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AccessGroupCommandDto>)pair.Value);
    }

    private static async Task<string?> FindAccessGroupKeyColumnAsync(
        SqlConnection cn,
        CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(
                cn,
                "dbo",
                "AccessGroups",
                ct))
        {
            return null;
        }

        var columns = await SqlHelpers.GetColumnsAsync(
            cn,
            "dbo",
            "AccessGroups",
            ct);

        // xAccessRights.AccessGroupID references a numeric surrogate key.
        // Prefer conventional numeric key names before considering a PK
        // that might be the Guid column.
        foreach (var candidate in new[]
                 {
                     "AccessGroupID",
                     "ID",
                     "PK",
                     "s_PK",
                     "s_ObjectID"
                 })
        {
            if (columns.Contains(candidate))
                return candidate;
        }

        await using (var identityCommand = cn.CreateCommand())
        {
            identityCommand.CommandText = @"
SELECT TOP (1)
    name
FROM sys.columns
WHERE object_id = OBJECT_ID(N'dbo.AccessGroups')
  AND is_identity = 1
ORDER BY column_id;";

            var identity = Convert.ToString(
                await identityCommand.ExecuteScalarAsync(ct));

            if (!string.IsNullOrWhiteSpace(identity))
                return identity;
        }

        await using var primaryKeyCommand = cn.CreateCommand();
        primaryKeyCommand.CommandText = @"
SELECT TOP (1)
    c.name
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
  AND t.name IN (
      N'tinyint',
      N'smallint',
      N'int',
      N'bigint',
      N'numeric',
      N'decimal'
  )
ORDER BY ic.key_ordinal;";

        var primaryKey = Convert.ToString(
            await primaryKeyCommand.ExecuteScalarAsync(ct));

        return string.IsNullOrWhiteSpace(primaryKey)
            ? null
            : primaryKey;
    }

    private static string GetScope(RawRightRow row)
    {
        if (row.ObjectId != 0 || row.XObjectId != 0)
            return "object";

        if (row.StageId != 0)
            return "stage";

        if (row.LinkId != 0 || row.LinkTypeId != 0)
            return "link";

        if (row.ReferenceId != 0 || row.XReferenceId != 0)
            return "reference";

        return "global";
    }

    private static int ReadInt(
        SqlDataReader reader,
        string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static int? ReadNullableInt(
        SqlDataReader reader,
        string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static long ReadLong(
        SqlDataReader reader,
        string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt64(reader.GetValue(ordinal));
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
        int XAuthorId,
        int XReferenceId,
        long XObjectId,
        long XAccessObjectId,
        int XReferenceAuthorGroupId);
}
