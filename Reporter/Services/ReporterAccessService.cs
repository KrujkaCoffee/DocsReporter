using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface IReporterAccessService
{
    Task<int> EnsureAppUserAsync(ClaimsPrincipal user, CancellationToken ct);
    Task<EffectiveGroupAccessDto?> GetGroupAccessAsync(ClaimsPrincipal user, string sourceCode, int groupId, CancellationToken ct);
    Task<IReadOnlyList<EffectiveRelationAccessDto>> GetAllowedRelationsAsync(ClaimsPrincipal user, string sourceCode, int groupId, CancellationToken ct);
}

public sealed class ReporterAccessService : IReporterAccessService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly ReporterOptions _options;

    public ReporterAccessService(IReporterSqlConnectionFactory factory, IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<int> EnsureAppUserAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var login = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(login))
            login = "anonymous";

        var sid = user.FindFirst(ClaimTypes.PrimarySid)?.Value;

        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @User table (AppUserId int);

MERGE app.AppUser AS target
USING (SELECT @login AS WindowsLogin, @sid AS WindowsSid) AS src
ON target.WindowsLogin = src.WindowsLogin
WHEN MATCHED THEN UPDATE SET WindowsSid = COALESCE(src.WindowsSid, target.WindowsSid)
WHEN NOT MATCHED THEN INSERT(WindowsLogin, WindowsSid, IsEnabled) VALUES(src.WindowsLogin, src.WindowsSid, 1)
OUTPUT inserted.AppUserId INTO @User;

DECLARE @AppUserId int = (SELECT TOP (1) AppUserId FROM @User);
DECLARE @RoleId int = (SELECT AppRoleId FROM app.AppRole WHERE Code = @defaultRoleCode);

IF @RoleId IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM app.AppUserRole WHERE AppUserId = @AppUserId AND AppRoleId = @RoleId)
BEGIN
    INSERT INTO app.AppUserRole(AppUserId, AppRoleId) VALUES(@AppUserId, @RoleId);
END

SELECT @AppUserId;";
        cmd.Parameters.Add(new SqlParameter("@login", SqlDbType.NVarChar, 255) { Value = login });
        cmd.Parameters.Add(new SqlParameter("@sid", SqlDbType.NVarChar, 255) { Value = (object?)sid ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@defaultRoleCode", SqlDbType.NVarChar, 100) { Value = _options.DefaultRoleCode });

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<EffectiveGroupAccessDto?> GetGroupAccessAsync(ClaimsPrincipal user, string sourceCode, int groupId, CancellationToken ct)
    {
        var appUserId = await EnsureAppUserAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @SourceId int = (SELECT SourceId FROM app.Source WHERE Code = @sourceCode AND IsEnabled = 1);

SELECT TOP (1)
    CAST(MAX(CAST(p.CanSeeInMenu AS int)) AS bit) AS CanSeeInMenu,
    CAST(MAX(CAST(p.CanSearch AS int)) AS bit) AS CanSearch,
    CAST(MAX(CAST(p.CanOpenCard AS int)) AS bit) AS CanOpenCard,
    CAST(MAX(CAST(p.CanExport AS int)) AS bit) AS CanExport,
    MAX(p.MaxObjectDepth) AS MaxObjectDepth,
    MAX(p.MaxFileTreeDepth) AS MaxFileTreeDepth,
    MAX(p.MaxRowsPerPage) AS MaxRowsPerPage,
    MAX(p.MaxRelatedObjects) AS MaxRelatedObjects
FROM app.GroupAccessPolicy p
JOIN app.AppUserRole ur ON ur.AppRoleId = p.AppRoleId
WHERE ur.AppUserId = @appUserId
  AND p.IsEnabled = 1
  AND p.GroupId = @groupId
  AND (p.SourceId IS NULL OR p.SourceId = @SourceId);";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });
        cmd.Parameters.Add(new SqlParameter("@appUserId", SqlDbType.Int) { Value = appUserId });

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.IsDBNull(0))
            return null;

        return new EffectiveGroupAccessDto(
            r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3),
            r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7));
    }

    public async Task<IReadOnlyList<EffectiveRelationAccessDto>> GetAllowedRelationsAsync(ClaimsPrincipal user, string sourceCode, int groupId, CancellationToken ct)
    {
        var appUserId = await EnsureAppUserAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @SourceId int = (SELECT SourceId FROM app.Source WHERE Code = @sourceCode AND IsEnabled = 1);

SELECT
    p.RelationGroupId,
    p.RelationTable,
    p.DisplayName,
    p.CategoryCode,
    p.CategoryTitle,
    CAST(MAX(CAST(p.CanTraverse AS int)) AS bit) AS CanTraverse,
    CAST(MAX(CAST(p.ShowInCard AS int)) AS bit) AS ShowInCard,
    CAST(MAX(CAST(p.ShowInTree AS int)) AS bit) AS ShowInTree,
    MAX(p.MaxDepth) AS MaxDepth,
    MAX(p.MaxItems) AS MaxItems,
    MAX(p.DirectionMode) AS DirectionMode
FROM app.RelationAccessPolicy p
JOIN app.AppUserRole ur ON ur.AppRoleId = p.AppRoleId
WHERE ur.AppUserId = @appUserId
  AND p.IsEnabled = 1
  AND p.CanTraverse = 1
  AND (p.SourceId IS NULL OR p.SourceId = @SourceId)
  AND EXISTS (
      SELECT 1
      FROM app.TflexRelation r
      WHERE r.SourceId = @SourceId
        AND r.RelationTable = p.RelationTable
        AND (r.MasterGroupId = @groupId OR r.SlaveGroupId = @groupId)
  )
GROUP BY p.RelationGroupId, p.RelationTable, p.DisplayName, p.CategoryCode, p.CategoryTitle
ORDER BY COALESCE(p.CategoryTitle, p.DisplayName, p.RelationTable);";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });
        cmd.Parameters.Add(new SqlParameter("@appUserId", SqlDbType.Int) { Value = appUserId });

        var list = new List<EffectiveRelationAccessDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new EffectiveRelationAccessDto(
                r.IsDBNull(0) ? null : r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7),
                r.GetInt32(8), r.GetInt32(9), r.GetString(10)));
        }
        return list;
    }
}
