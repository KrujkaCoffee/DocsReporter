using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface IReporterIdentityService
{
    Task<ReporterCurrentUserDto> GetCurrentAsync(
        ClaimsPrincipal user,
        IReadOnlyCollection<string>? sourceCodes,
        CancellationToken ct);

    Task<ReporterSourceIdentityDto> ResolveSourceAsync(
        ClaimsPrincipal user,
        string sourceCode,
        CancellationToken ct);
}

public sealed class ReporterIdentityService : IReporterIdentityService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _access;
    private readonly ReporterOptions _options;

    public ReporterIdentityService(
        IReporterSqlConnectionFactory factory,
        IReporterAccessService access,
        IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _access = access;
        _options = options.Value;
    }

    public async Task<ReporterCurrentUserDto> GetCurrentAsync(
        ClaimsPrincipal user,
        IReadOnlyCollection<string>? sourceCodes,
        CancellationToken ct)
    {
        var principal = ReporterPrincipalResolver.Resolve(user, _options);
        var appUserId = await _access.EnsureAppUserAsync(user, ct);
        var roles = await ReadAppRolesAsync(appUserId, ct);
        IReadOnlyList<SourceRow> catalog = await ReadSourcesAsync(ct);

        if (sourceCodes is { Count: > 0 })
        {
            var requested = sourceCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            catalog = catalog.Where(source => requested.Contains(source.Code)).ToArray();
        }

        var sources = new List<ReporterSourceIdentityDto>(catalog.Count);
        foreach (var source in catalog)
        {
            sources.Add(await ResolveSourceCoreAsync(
                principal,
                appUserId,
                source,
                ct));
        }

        return new ReporterCurrentUserDto(
            principal.Login,
            principal.Sid,
            principal.IsAuthenticated,
            principal.AuthenticationType,
            principal.IsDebugIdentity,
            appUserId,
            roles,
            _options.SecurityMode,
            sources);
    }

    public async Task<ReporterSourceIdentityDto> ResolveSourceAsync(
        ClaimsPrincipal user,
        string sourceCode,
        CancellationToken ct)
    {
        var principal = ReporterPrincipalResolver.Resolve(user, _options);
        var appUserId = await _access.EnsureAppUserAsync(user, ct);
        var source = await ReadSourceAsync(sourceCode, ct);

        if (source is null)
        {
            return new ReporterSourceIdentityDto(
                sourceCode,
                sourceCode,
                "source_not_found",
                null,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<TflexUserHierarchyNodeDto>(),
                $"Source '{sourceCode}' was not found in app.Source.");
        }

        return await ResolveSourceCoreAsync(principal, appUserId, source, ct);
    }

    private async Task<ReporterSourceIdentityDto> ResolveSourceCoreAsync(
        ReporterPrincipalSnapshot principal,
        int appUserId,
        SourceRow source,
        CancellationToken ct)
    {
        try
        {
            await using var cn = await _factory.OpenSourceConnectionAsync(source.Code, ct);

            if (!await SqlHelpers.TableExistsAsync(cn, "dbo", "Users", ct) ||
                !await SqlHelpers.TableExistsAsync(cn, "dbo", "UserParameters", ct))
            {
                return Error(source, "schema_missing", "Users/UserParameters tables were not found.");
            }

            var userRow = await FindDocsUserAsync(cn, principal, ct);
            if (userRow is null)
            {
                return new ReporterSourceIdentityDto(
                    source.Code,
                    source.DisplayName,
                    "not_found",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<TflexUserHierarchyNodeDto>(),
                    "T-FLEX user was not matched by SID or login.");
            }

            var hierarchy = await ReadHierarchyAsync(cn, userRow.ObjectId, ct);
            await UpsertSourceUserMapAsync(source.SourceId, appUserId, userRow, ct);

            return new ReporterSourceIdentityDto(
                source.Code,
                source.DisplayName,
                "matched",
                userRow.MatchMethod,
                userRow.ObjectId,
                userRow.Guid,
                userRow.Login,
                userRow.FullName,
                userRow.Email,
                hierarchy,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(source, "error", SafeError(ex));
        }
    }

    private async Task<DocsUserRow?> FindDocsUserAsync(
        SqlConnection cn,
        ReporterPrincipalSnapshot principal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(principal.Sid) &&
            principal.CandidateLogins.Count == 0)
        {
            return null;
        }

        var parameterColumns = await SqlHelpers.GetColumnsAsync(
            cn,
            "dbo",
            "UserParameters",
            ct);

        var alternateLoginColumn = parameterColumns.Contains("N_77BD1F9D7F42CDB")
            ? "N_77BD1F9D7F42CDB"
            : null;

        var loginParameters = new List<SqlParameter>();
        var loginNames = new List<string>();

        for (var i = 0; i < principal.CandidateLogins.Count; i++)
        {
            var name = $"@login{i}";
            loginNames.Add(name);
            loginParameters.Add(new SqlParameter(name, SqlDbType.NVarChar, 255)
            {
                Value = principal.CandidateLogins[i].ToUpperInvariant()
            });
        }

        var loginList = loginNames.Count == 0
            ? "N''"
            : string.Join(",", loginNames);

        var loginMatch = loginNames.Count == 0
            ? "1 = 0"
            : $"UPPER(LTRIM(RTRIM(TRY_CONVERT(nvarchar(255), p.[Login])))) IN ({loginList})";

        var alternateMatch = alternateLoginColumn is null || loginNames.Count == 0
            ? "1 = 0"
            : $"UPPER(LTRIM(RTRIM(TRY_CONVERT(nvarchar(255), p.{SqlHelpers.QuoteName(alternateLoginColumn)})))) IN ({loginList})";

        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT TOP (1)
    TRY_CONVERT(bigint, u.s_ObjectID) AS ObjectId,
    TRY_CONVERT(nvarchar(36), u.s_Guid) AS Guid,
    TRY_CONVERT(nvarchar(255), u.FullName) AS FullName,
    TRY_CONVERT(nvarchar(255), p.[Login]) AS Login,
    TRY_CONVERT(nvarchar(255), p.SID) AS Sid,
    TRY_CONVERT(nvarchar(255), p.EMail) AS Email,
    CASE
        WHEN @sid IS NOT NULL
         AND LTRIM(RTRIM(TRY_CONVERT(nvarchar(255), p.SID))) = @sid
            THEN N'SID'
        WHEN {loginMatch}
            THEN N'Login'
        WHEN {alternateMatch}
            THEN N'Alias'
        ELSE N'Unknown'
    END AS MatchMethod
FROM dbo.Users u
JOIN dbo.UserParameters p
  ON p.s_ObjectID = u.s_ObjectID
 AND p.s_ActualVersion = 1
 AND p.s_Deleted = 0
WHERE u.s_ActualVersion = 1
  AND u.s_Deleted = 0
  AND (
       (@sid IS NOT NULL
        AND LTRIM(RTRIM(TRY_CONVERT(nvarchar(255), p.SID))) = @sid)
       OR ({loginMatch})
       OR ({alternateMatch})
  )
ORDER BY CASE
    WHEN @sid IS NOT NULL
     AND LTRIM(RTRIM(TRY_CONVERT(nvarchar(255), p.SID))) = @sid
        THEN 0
    WHEN {loginMatch}
        THEN 1
    WHEN {alternateMatch}
        THEN 2
    ELSE 9
END,
TRY_CONVERT(bigint, u.s_ObjectID);";

        cmd.Parameters.Add(new SqlParameter("@sid", SqlDbType.NVarChar, 255)
        {
            Value = (object?)principal.Sid ?? DBNull.Value
        });

        foreach (var parameter in loginParameters)
            cmd.Parameters.Add(parameter);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new DocsUserRow(
            reader.GetInt64Flexible("ObjectId"),
            reader.GetNullableString("Guid"),
            reader.GetNullableString("FullName"),
            reader.GetNullableString("Login"),
            reader.GetNullableString("Sid"),
            reader.GetNullableString("Email"),
            reader.GetNullableString("MatchMethod") ?? "Unknown");
    }

    private async Task<IReadOnlyList<TflexUserHierarchyNodeDto>> ReadHierarchyAsync(
        SqlConnection cn,
        long userObjectId,
        CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(
                cn,
                "dbo",
                "UsersHierarchy",
                ct))
        {
            return Array.Empty<TflexUserHierarchyNodeDto>();
        }

        var maxDepth = Math.Clamp(_options.SecurityMaxHierarchyDepth, 1, 64);

        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = @"
;WITH Hierarchy AS (
    SELECT
        CAST(@userId AS bigint) AS ObjectId,
        CAST(NULL AS bigint) AS ChildObjectId,
        CAST(0 AS int) AS Depth,
        CAST(1 AS bit) AS IsFirstUse,
        CAST(
            N'/' + CONVERT(nvarchar(30), @userId) + N'/'
            AS nvarchar(max)
        ) AS Path

    UNION ALL

    SELECT
        TRY_CONVERT(bigint, uh.s_ParentID) AS ObjectId,
        h.ObjectId AS ChildObjectId,
        h.Depth + 1 AS Depth,
        TRY_CONVERT(bit, uh.s_FirstUse) AS IsFirstUse,
        CAST(
            h.Path
            + CONVERT(nvarchar(30), uh.s_ParentID)
            + N'/'
            AS nvarchar(max)
        ) AS Path
    FROM Hierarchy h
    JOIN dbo.UsersHierarchy uh
      ON TRY_CONVERT(bigint, uh.s_ObjectID) = h.ObjectId
     AND uh.s_ActualVersion = 1
     AND uh.s_Deleted = 0
    WHERE h.Depth < @maxDepth
      AND TRY_CONVERT(bigint, uh.s_ParentID) IS NOT NULL
      AND TRY_CONVERT(bigint, uh.s_ParentID) <> 0
      AND CHARINDEX(
            N'/' + CONVERT(nvarchar(30), uh.s_ParentID) + N'/',
            h.Path
          ) = 0
)
SELECT DISTINCT
    h.ObjectId,
    h.ChildObjectId,
    h.Depth,
    h.IsFirstUse,
    TRY_CONVERT(nvarchar(36), u.s_Guid) AS Guid,
    TRY_CONVERT(nvarchar(255), u.FullName) AS FullName,
    TRY_CONVERT(nvarchar(255), p.[Login]) AS Login
FROM Hierarchy h
LEFT JOIN dbo.Users u
  ON TRY_CONVERT(bigint, u.s_ObjectID) = h.ObjectId
 AND u.s_ActualVersion = 1
 AND u.s_Deleted = 0
LEFT JOIN dbo.UserParameters p
  ON p.s_ObjectID = u.s_ObjectID
 AND p.s_ActualVersion = 1
 AND p.s_Deleted = 0
ORDER BY h.Depth, h.ObjectId
OPTION (MAXRECURSION 64);";

        cmd.Parameters.Add(new SqlParameter("@userId", SqlDbType.BigInt)
        {
            Value = userObjectId
        });
        cmd.Parameters.Add(new SqlParameter("@maxDepth", SqlDbType.Int)
        {
            Value = maxDepth
        });

        var result = new List<TflexUserHierarchyNodeDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var depth = reader.GetInt32Flexible("Depth");
            var childOrdinal = reader.GetOrdinal("ChildObjectId");
            var firstUseOrdinal = reader.GetOrdinal("IsFirstUse");

            result.Add(new TflexUserHierarchyNodeDto(
                reader.GetInt64Flexible("ObjectId"),
                reader.IsDBNull(childOrdinal)
                    ? null
                    : reader.GetInt64Flexible("ChildObjectId"),
                depth,
                depth == 0,
                !reader.IsDBNull(firstUseOrdinal)
                && Convert.ToBoolean(reader.GetValue(firstUseOrdinal)),
                reader.GetNullableString("Guid"),
                reader.GetNullableString("FullName"),
                reader.GetNullableString("Login")));
        }

        return result;
    }

    private async Task UpsertSourceUserMapAsync(
        int sourceId,
        int appUserId,
        DocsUserRow user,
        CancellationToken ct)
    {
        object docsGuid = DBNull.Value;
        if (Guid.TryParse(user.Guid, out var parsedGuid))
            docsGuid = parsedGuid;

        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
MERGE app.SourceUserMap AS target
USING (
    SELECT
        @sourceId AS SourceId,
        @appUserId AS AppUserId
) AS src
ON target.SourceId = src.SourceId
AND target.AppUserId = src.AppUserId
WHEN MATCHED THEN UPDATE SET
    DocsUserObjectId = @docsUserObjectId,
    DocsUserGuid = @docsUserGuid,
    DocsLogin = @docsLogin,
    DocsFullName = @docsFullName,
    DocsEmail = @docsEmail,
    LastResolvedAt = sysdatetime()
WHEN NOT MATCHED THEN INSERT(
    SourceId,
    AppUserId,
    DocsUserObjectId,
    DocsUserGuid,
    DocsLogin,
    DocsFullName,
    DocsEmail,
    LastResolvedAt
)
VALUES(
    @sourceId,
    @appUserId,
    @docsUserObjectId,
    @docsUserGuid,
    @docsLogin,
    @docsFullName,
    @docsEmail,
    sysdatetime()
);

UPDATE app.AppUser
SET DisplayName = COALESCE(@docsFullName, DisplayName),
    Email = COALESCE(@docsEmail, Email)
WHERE AppUserId = @appUserId;";

        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int)
        {
            Value = sourceId
        });
        cmd.Parameters.Add(new SqlParameter("@appUserId", SqlDbType.Int)
        {
            Value = appUserId
        });
        cmd.Parameters.Add(new SqlParameter("@docsUserObjectId", SqlDbType.BigInt)
        {
            Value = user.ObjectId
        });
        cmd.Parameters.Add(new SqlParameter("@docsUserGuid", SqlDbType.UniqueIdentifier)
        {
            Value = docsGuid
        });
        cmd.Parameters.Add(new SqlParameter("@docsLogin", SqlDbType.NVarChar, 255)
        {
            Value = (object?)user.Login ?? DBNull.Value
        });
        cmd.Parameters.Add(new SqlParameter("@docsFullName", SqlDbType.NVarChar, 255)
        {
            Value = (object?)user.FullName ?? DBNull.Value
        });
        cmd.Parameters.Add(new SqlParameter("@docsEmail", SqlDbType.NVarChar, 255)
        {
            Value = (object?)user.Email ?? DBNull.Value
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<string>> ReadAppRolesAsync(
        int appUserId,
        CancellationToken ct)
    {
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT r.Code
FROM app.AppUserRole ur
JOIN app.AppRole r
  ON r.AppRoleId = ur.AppRoleId
WHERE ur.AppUserId = @appUserId
ORDER BY r.Code;";

        cmd.Parameters.Add(new SqlParameter("@appUserId", SqlDbType.Int)
        {
            Value = appUserId
        });

        var roles = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            roles.Add(reader.GetString(0));

        return roles;
    }

    private async Task<IReadOnlyList<SourceRow>> ReadSourcesAsync(
        CancellationToken ct)
    {
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT SourceId, Code, DisplayName
FROM app.Source
WHERE IsEnabled = 1
ORDER BY DisplayName, Code;";

        var sources = new List<SourceRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            sources.Add(new SourceRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return sources;
    }

    private async Task<SourceRow?> ReadSourceAsync(
        string sourceCode,
        CancellationToken ct)
    {
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT TOP (1)
    SourceId,
    Code,
    DisplayName
FROM app.Source
WHERE Code = @sourceCode
  AND IsEnabled = 1;";

        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100)
        {
            Value = sourceCode
        });

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new SourceRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2))
            : null;
    }

    private static ReporterSourceIdentityDto Error(
        SourceRow source,
        string status,
        string error) =>
        new(
            source.Code,
            source.DisplayName,
            status,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<TflexUserHierarchyNodeDto>(),
            error);

    private static string SafeError(Exception ex)
    {
        var message = ex.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return message.Length <= 500
            ? message
            : message[..500] + "…";
    }

    private sealed record SourceRow(
        int SourceId,
        string Code,
        string DisplayName);

    private sealed record DocsUserRow(
        long ObjectId,
        string? Guid,
        string? FullName,
        string? Login,
        string? Sid,
        string? Email,
        string MatchMethod);
}
