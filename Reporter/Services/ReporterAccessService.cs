using System.Data;
using DocsApi.Reporter.StrictAccess;
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
    Task<IReadOnlyList<EffectiveRelationAccessDto>> GetAllowedFileRelationsAsync(ClaimsPrincipal user, string sourceCode, CancellationToken ct);
}

public sealed class ReporterAccessService : IReporterAccessService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly ReporterOptions _options;
    private readonly StrictRuntimeOptions _runtime;
    private readonly Dictionary<string, int> _requestUsers = new(StringComparer.OrdinalIgnoreCase);

    public ReporterAccessService(IReporterSqlConnectionFactory factory, IOptions<ReporterOptions> options, IOptions<StrictRuntimeOptions> runtime)
    {
        _factory = factory;
        _options = options.Value;
        _runtime = runtime.Value;
    }

    public async Task<int> EnsureAppUserAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var actor = RuntimePrincipal.Read(user) ?? throw new UnauthorizedAccessException();
        if (_requestUsers.TryGetValue(actor.Sid, out var existing)) return existing;
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var cmd = cn.CreateCommand(); cmd.Transaction = tx; cmd.CommandTimeout = 15;
        cmd.CommandText = @"
DECLARE @id int, @count int, @oldSid nvarchar(255), @enabled bit;
SELECT @count=COUNT(*),@id=MIN(AppUserId) FROM app.AppUser WITH(UPDLOCK,HOLDLOCK)
 WHERE WindowsSid=@sid OR WindowsLogin=@login;
IF @count>1 THROW 51082,N'Неоднозначное сопоставление пользователя репортера.',1;
IF @count=1
BEGIN
 SELECT @oldSid=WindowsSid,@enabled=IsEnabled FROM app.AppUser WHERE AppUserId=@id;
 IF @enabled IS NULL OR @enabled=0 THROW 51083,N'Учётная запись репортера отключена.',1;
 IF NULLIF(LTRIM(RTRIM(@oldSid)),N'') IS NOT NULL AND @oldSid<>@sid
   THROW 51084,N'SID не совпадает с ранее сохранённой учётной записью.',1;
 UPDATE app.AppUser SET WindowsLogin=@login,WindowsSid=@sid WHERE AppUserId=@id;
END
ELSE
BEGIN
 IF @provision=0 THROW 51085,N'Пользователь репортера не зарегистрирован.',1;
 INSERT INTO app.AppUser(WindowsLogin,WindowsSid,IsEnabled) VALUES(@login,@sid,1);
 SET @id=CONVERT(int,SCOPE_IDENTITY());
 -- Only on first creation; removed roles are NOT re-created on subsequent requests.
 INSERT INTO app.AppUserRole(AppUserId,AppRoleId)
 SELECT @id,AppRoleId FROM app.AppRole WHERE Code=N'ProjectCardViewer';
END;
SELECT @id;";
        cmd.Parameters.Add(new SqlParameter("@login", SqlDbType.NVarChar, 255) { Value=actor.Login });
        cmd.Parameters.Add(new SqlParameter("@sid", SqlDbType.NVarChar, 255) { Value=actor.Sid });
        cmd.Parameters.Add(new SqlParameter("@provision", SqlDbType.Bit) { Value=_runtime.ProvisionNewUsers });
        try
        {
            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            await tx.CommitAsync(ct); _requestUsers[actor.Sid]=id; return id;
        }
        catch (SqlException ex) when (ex.Number is >= 51082 and <= 51085)
        { throw new UnauthorizedAccessException("Учётная запись не подтверждена или отключена.", ex); }
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
  AND p.SourceId = @SourceId;";
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
  AND p.SourceId = @SourceId
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

    public async Task<IReadOnlyList<EffectiveRelationAccessDto>> GetAllowedFileRelationsAsync(ClaimsPrincipal user, string sourceCode, CancellationToken ct)
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
  AND p.SourceId = @SourceId
  AND EXISTS (
      SELECT 1
      FROM app.TflexRelation r
      WHERE r.SourceId = @SourceId
        AND r.RelationTable = p.RelationTable
  )
  AND (
      p.ShowInTree = 1
      OR p.CategoryCode IN (
          N'tz', N'tz_object', N'tz_root',
          N'vo_cad', N'vo_pdf', N'root', N'doc', N'control', N'tests',
          N'documents', N'documents_root', N'documents_files',
          N'nomenclature', N'nomenclature_documents'
      )
      OR p.RelationTable IN (
          N'Tekhnicheskoe_zadanie_Kar', N'Papka_TZ_Tekhnicheskoe_za', N'Link_899_16', N'Papka_TZ_kartochka_pr_1',
          N'Link_891_403_1', N'Link_403_891_1', N'Link_891_403',
          N'Link_891_1802', N'Link_1802_16_1', N'Link_1802_16_2',
          N'DocumentFiles', N'Link_Documents_Files', N'Link_Documents_Files_1'
      )
  )
GROUP BY p.RelationGroupId, p.RelationTable, p.DisplayName, p.CategoryCode, p.CategoryTitle
ORDER BY COALESCE(p.CategoryTitle, p.DisplayName, p.RelationTable);";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
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
