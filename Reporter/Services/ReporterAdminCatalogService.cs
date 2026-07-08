using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface IReporterAdminCatalogService
{
    Task EnsureAdminAsync(ClaimsPrincipal user, CancellationToken ct);

    Task<IReadOnlyList<SourceDto>> GetSourcesAsync(CancellationToken ct);

    Task<IReadOnlyList<ReporterAdminRoleDto>> GetRolesAsync(
        ClaimsPrincipal user,
        CancellationToken ct);

    Task<ReporterAdminOverviewDto> GetOverviewAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        CancellationToken ct);

    Task<IReadOnlyList<ReporterAdminGroupPolicyDto>> GetGroupsAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        string? query,
        bool enabledOnly,
        int take,
        CancellationToken ct);

    Task<ReporterAdminGroupPolicyDto?> GetGroupAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int groupId,
        CancellationToken ct);

    Task<ReporterAdminGroupPolicyDto> UpdateGroupPolicyAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int groupId,
        ReporterAdminGroupPolicyUpdateDto request,
        CancellationToken ct);

    Task<IReadOnlyList<ReporterAdminRelationPolicyDto>> GetRelationsAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int? groupId,
        string? query,
        bool enabledOnly,
        int take,
        CancellationToken ct);

    Task<ReporterAdminRelationPolicyDto?> GetRelationAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int relationGroupId,
        CancellationToken ct);

    Task<ReporterAdminRelationPolicyDto> UpdateRelationPolicyAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int relationGroupId,
        ReporterAdminRelationPolicyUpdateDto request,
        CancellationToken ct);
}

public sealed class ReporterAdminCatalogService : IReporterAdminCatalogService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _access;
    private readonly ReporterOptions _options;

    public ReporterAdminCatalogService(
        IReporterSqlConnectionFactory factory,
        IReporterAccessService access,
        IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _access = access;
        _options = options.Value;
    }

    public async Task EnsureAdminAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (_options.AdminAllowAnonymousInPreview
            && _options.SecurityMode.Equals("Preview", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var appUserId = await _access.EnsureAppUserAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (1) 1
FROM app.AppUserRole ur
JOIN app.AppRole r ON r.AppRoleId = ur.AppRoleId
WHERE ur.AppUserId = @appUserId
  AND r.Code IN (N'ReportAdmin', N'Developer');";
        cmd.Parameters.Add(new SqlParameter("@appUserId", SqlDbType.Int) { Value = appUserId });
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null || value is DBNull)
            throw new UnauthorizedAccessException("Reporter admin access requires ReportAdmin or Developer role.");
    }

    public async Task<IReadOnlyList<SourceDto>> GetSourcesAsync(CancellationToken ct)
    {
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT SourceId, Code, DisplayName, BaseDocsUrl, IsEnabled
FROM app.Source
ORDER BY DisplayName, Code;";

        var result = new List<SourceDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SourceDto(
                ReadInt(reader, "SourceId"),
                reader.GetString(reader.GetOrdinal("Code")),
                reader.GetNullableString("DisplayName") ?? reader.GetString(reader.GetOrdinal("Code")),
                reader.GetNullableString("BaseDocsUrl"),
                ReadBool(reader, "IsEnabled")));
        }

        return result;
    }

    public async Task<IReadOnlyList<ReporterAdminRoleDto>> GetRolesAsync(
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT AppRoleId, Code, DisplayName, IsSystem
FROM app.AppRole
ORDER BY IsSystem DESC, DisplayName, Code;";

        var result = new List<ReporterAdminRoleDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ReporterAdminRoleDto(
                ReadInt(reader, "AppRoleId"),
                reader.GetString(reader.GetOrdinal("Code")),
                reader.GetNullableString("DisplayName") ?? reader.GetString(reader.GetOrdinal("Code")),
                ReadBool(reader, "IsSystem")));
        }

        return result;
    }

    public async Task<ReporterAdminOverviewDto> GetOverviewAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM app.Source WHERE IsEnabled = 1) AS SourceCount,
    (SELECT COUNT(*) FROM app.AppRole) AS RoleCount,
    (SELECT COUNT(*) FROM app.TflexGroup WHERE SourceId = @sourceId) AS TotalGroupCount,
    (SELECT COUNT(*) FROM app.GroupAccessPolicy WHERE SourceId = @sourceId AND AppRoleId = @roleId AND IsEnabled = 1) AS EnabledGroupPolicyCount,
    (SELECT COUNT(*) FROM app.TflexRelation WHERE SourceId = @sourceId) AS TotalRelationCount,
    (SELECT COUNT(*) FROM app.RelationAccessPolicy WHERE SourceId = @sourceId AND AppRoleId = @roleId AND IsEnabled = 1) AS EnabledRelationPolicyCount;";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@roleId", SqlDbType.Int) { Value = roleId });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new ReporterAdminOverviewDto(
            sourceCode,
            roleCode,
            ReadInt(reader, "SourceCount"),
            ReadInt(reader, "RoleCount"),
            ReadInt(reader, "TotalGroupCount"),
            ReadInt(reader, "EnabledGroupPolicyCount"),
            ReadInt(reader, "TotalRelationCount"),
            ReadInt(reader, "EnabledRelationPolicyCount"),
            _options.SecurityMode,
            _options.AdminAllowAnonymousInPreview
            && _options.SecurityMode.Equals("Preview", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ReporterAdminGroupPolicyDto>> GetGroupsAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        string? query,
        bool enabledOnly,
        int take,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);
        return await ReadGroupsAsync(cn, sourceCode, sourceId, roleCode, roleId, null, query, enabledOnly, take, ct);
    }

    public async Task<ReporterAdminGroupPolicyDto?> GetGroupAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int groupId,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);
        return (await ReadGroupsAsync(cn, sourceCode, sourceId, roleCode, roleId, groupId, null, false, 1, ct)).FirstOrDefault();
    }

    public async Task<ReporterAdminGroupPolicyDto> UpdateGroupPolicyAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int groupId,
        ReporterAdminGroupPolicyUpdateDto request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);
        var tableName = await GetGroupTableNameAsync(cn, sourceId, groupId, ct);

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @OldIsEnabled bit = NULL;
DECLARE @OldCanSeeInMenu bit = NULL;
DECLARE @OldCanSearch bit = NULL;
DECLARE @OldCanOpenCard bit = NULL;
DECLARE @OldCanExport bit = NULL;
DECLARE @OldMaxObjectDepth int = NULL;
DECLARE @OldMaxFileTreeDepth int = NULL;
DECLARE @OldMaxRowsPerPage int = NULL;
DECLARE @OldMaxRelatedObjects int = NULL;

SELECT TOP (1)
    @OldIsEnabled = IsEnabled,
    @OldCanSeeInMenu = CanSeeInMenu,
    @OldCanSearch = CanSearch,
    @OldCanOpenCard = CanOpenCard,
    @OldCanExport = CanExport,
    @OldMaxObjectDepth = MaxObjectDepth,
    @OldMaxFileTreeDepth = MaxFileTreeDepth,
    @OldMaxRowsPerPage = MaxRowsPerPage,
    @OldMaxRelatedObjects = MaxRelatedObjects
FROM app.GroupAccessPolicy
WHERE SourceId = @sourceId
  AND AppRoleId = @roleId
  AND GroupId = @groupId;

MERGE app.GroupAccessPolicy AS target
USING (
    SELECT
        @sourceId AS SourceId,
        @roleId AS AppRoleId,
        @groupId AS GroupId,
        @tableName AS TableName,
        COALESCE(@canSeeInMenu, @OldCanSeeInMenu, CAST(0 AS bit)) AS CanSeeInMenu,
        COALESCE(@canSearch, @OldCanSearch, CAST(0 AS bit)) AS CanSearch,
        COALESCE(@canOpenCard, @OldCanOpenCard, CAST(0 AS bit)) AS CanOpenCard,
        COALESCE(@canExport, @OldCanExport, CAST(0 AS bit)) AS CanExport,
        COALESCE(@maxObjectDepth, @OldMaxObjectDepth, 1) AS MaxObjectDepth,
        COALESCE(@maxFileTreeDepth, @OldMaxFileTreeDepth, 2) AS MaxFileTreeDepth,
        COALESCE(@maxRowsPerPage, @OldMaxRowsPerPage, 50) AS MaxRowsPerPage,
        COALESCE(@maxRelatedObjects, @OldMaxRelatedObjects, 200) AS MaxRelatedObjects,
        COALESCE(@isEnabled, @OldIsEnabled, CAST(1 AS bit)) AS IsEnabled
) AS src
ON target.SourceId = src.SourceId
AND target.AppRoleId = src.AppRoleId
AND target.GroupId = src.GroupId
WHEN MATCHED THEN UPDATE SET
    TableName = src.TableName,
    CanSeeInMenu = src.CanSeeInMenu,
    CanSearch = src.CanSearch,
    CanOpenCard = src.CanOpenCard,
    CanExport = src.CanExport,
    MaxObjectDepth = src.MaxObjectDepth,
    MaxFileTreeDepth = src.MaxFileTreeDepth,
    MaxRowsPerPage = src.MaxRowsPerPage,
    MaxRelatedObjects = src.MaxRelatedObjects,
    IsEnabled = src.IsEnabled
WHEN NOT MATCHED THEN INSERT(
    SourceId, AppRoleId, GroupId, TableName, CanSeeInMenu, CanSearch, CanOpenCard, CanExport,
    MaxObjectDepth, MaxFileTreeDepth, MaxRowsPerPage, MaxRelatedObjects, IsEnabled)
VALUES(
    src.SourceId, src.AppRoleId, src.GroupId, src.TableName, src.CanSeeInMenu, src.CanSearch, src.CanOpenCard, src.CanExport,
    src.MaxObjectDepth, src.MaxFileTreeDepth, src.MaxRowsPerPage, src.MaxRelatedObjects, src.IsEnabled);";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@roleId", SqlDbType.Int) { Value = roleId });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });
        cmd.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = tableName });
        AddNullableBit(cmd, "@isEnabled", request.IsEnabled);
        AddNullableBit(cmd, "@canSeeInMenu", request.CanSeeInMenu);
        AddNullableBit(cmd, "@canSearch", request.CanSearch);
        AddNullableBit(cmd, "@canOpenCard", request.CanOpenCard);
        AddNullableBit(cmd, "@canExport", request.CanExport);
        AddNullableInt(cmd, "@maxObjectDepth", request.MaxObjectDepth is null ? null : Math.Clamp(request.MaxObjectDepth.Value, 0, 3));
        AddNullableInt(cmd, "@maxFileTreeDepth", request.MaxFileTreeDepth is null ? null : Math.Clamp(request.MaxFileTreeDepth.Value, 0, 6));
        AddNullableInt(cmd, "@maxRowsPerPage", request.MaxRowsPerPage is null ? null : Math.Clamp(request.MaxRowsPerPage.Value, 1, 500));
        AddNullableInt(cmd, "@maxRelatedObjects", request.MaxRelatedObjects is null ? null : Math.Clamp(request.MaxRelatedObjects.Value, 1, 5000));
        await cmd.ExecuteNonQueryAsync(ct);

        return await GetGroupAsync(user, sourceCode, roleCode, groupId, ct)
            ?? throw new InvalidOperationException("Group policy was saved, but resulting row was not found.");
    }

    public async Task<IReadOnlyList<ReporterAdminRelationPolicyDto>> GetRelationsAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int? groupId,
        string? query,
        bool enabledOnly,
        int take,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);
        return await ReadRelationsAsync(cn, sourceCode, sourceId, roleCode, roleId, null, groupId, query, enabledOnly, take, ct);
    }

    public async Task<ReporterAdminRelationPolicyDto?> GetRelationAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int relationGroupId,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);
        return (await ReadRelationsAsync(cn, sourceCode, sourceId, roleCode, roleId, relationGroupId, null, null, false, 1, ct)).FirstOrDefault();
    }

    public async Task<ReporterAdminRelationPolicyDto> UpdateRelationPolicyAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string roleCode,
        int relationGroupId,
        ReporterAdminRelationPolicyUpdateDto request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(user, ct);
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        var sourceId = await GetSourceIdAsync(cn, sourceCode, ct);
        var roleId = await GetRoleIdAsync(cn, roleCode, ct);
        var relation = await GetRelationRowAsync(cn, sourceId, relationGroupId, ct);

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @OldIsEnabled bit = NULL;
DECLARE @OldDisplayName nvarchar(255) = NULL;
DECLARE @OldCategoryCode nvarchar(100) = NULL;
DECLARE @OldCategoryTitle nvarchar(255) = NULL;
DECLARE @OldCanTraverse bit = NULL;
DECLARE @OldShowInCard bit = NULL;
DECLARE @OldShowInTree bit = NULL;
DECLARE @OldMaxDepth int = NULL;
DECLARE @OldMaxItems int = NULL;
DECLARE @OldDirectionMode nvarchar(20) = NULL;

SELECT TOP (1)
    @OldIsEnabled = IsEnabled,
    @OldDisplayName = DisplayName,
    @OldCategoryCode = CategoryCode,
    @OldCategoryTitle = CategoryTitle,
    @OldCanTraverse = CanTraverse,
    @OldShowInCard = ShowInCard,
    @OldShowInTree = ShowInTree,
    @OldMaxDepth = MaxDepth,
    @OldMaxItems = MaxItems,
    @OldDirectionMode = DirectionMode
FROM app.RelationAccessPolicy
WHERE SourceId = @sourceId
  AND AppRoleId = @roleId
  AND RelationTable = @relationTable;

MERGE app.RelationAccessPolicy AS target
USING (
    SELECT
        @sourceId AS SourceId,
        @roleId AS AppRoleId,
        @relationGroupId AS RelationGroupId,
        @relationTable AS RelationTable,
        COALESCE(NULLIF(@displayName, N''), @OldDisplayName, @relationCaption, @relationTable) AS DisplayName,
        COALESCE(NULLIF(@categoryCode, N''), @OldCategoryCode, N'relation') AS CategoryCode,
        COALESCE(NULLIF(@categoryTitle, N''), @OldCategoryTitle, @relationCaption, @relationTable) AS CategoryTitle,
        COALESCE(@canTraverse, @OldCanTraverse, CAST(1 AS bit)) AS CanTraverse,
        COALESCE(@showInCard, @OldShowInCard, CAST(1 AS bit)) AS ShowInCard,
        COALESCE(@showInTree, @OldShowInTree, CAST(0 AS bit)) AS ShowInTree,
        COALESCE(@maxDepth, @OldMaxDepth, 1) AS MaxDepth,
        COALESCE(@maxItems, @OldMaxItems, 500) AS MaxItems,
        COALESCE(NULLIF(@directionMode, N''), @OldDirectionMode, N'Both') AS DirectionMode,
        COALESCE(@isEnabled, @OldIsEnabled, CAST(1 AS bit)) AS IsEnabled
) AS src
ON target.SourceId = src.SourceId
AND target.AppRoleId = src.AppRoleId
AND target.RelationTable = src.RelationTable
WHEN MATCHED THEN UPDATE SET
    RelationGroupId = src.RelationGroupId,
    DisplayName = src.DisplayName,
    CategoryCode = src.CategoryCode,
    CategoryTitle = src.CategoryTitle,
    CanTraverse = src.CanTraverse,
    ShowInCard = src.ShowInCard,
    ShowInTree = src.ShowInTree,
    MaxDepth = src.MaxDepth,
    MaxItems = src.MaxItems,
    DirectionMode = src.DirectionMode,
    IsEnabled = src.IsEnabled
WHEN NOT MATCHED THEN INSERT(
    SourceId, AppRoleId, RelationGroupId, RelationTable, DisplayName, CategoryCode, CategoryTitle,
    CanTraverse, ShowInCard, ShowInTree, MaxDepth, MaxItems, DirectionMode, IsEnabled)
VALUES(
    src.SourceId, src.AppRoleId, src.RelationGroupId, src.RelationTable, src.DisplayName, src.CategoryCode, src.CategoryTitle,
    src.CanTraverse, src.ShowInCard, src.ShowInTree, src.MaxDepth, src.MaxItems, src.DirectionMode, src.IsEnabled);";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@roleId", SqlDbType.Int) { Value = roleId });
        cmd.Parameters.Add(new SqlParameter("@relationGroupId", SqlDbType.Int) { Value = relation.RelationGroupId });
        cmd.Parameters.Add(new SqlParameter("@relationTable", SqlDbType.NVarChar, 128) { Value = relation.RelationTable });
        cmd.Parameters.Add(new SqlParameter("@relationCaption", SqlDbType.NVarChar, 255) { Value = (object?)relation.RelationCaption ?? DBNull.Value });
        AddNullableBit(cmd, "@isEnabled", request.IsEnabled);
        cmd.Parameters.Add(new SqlParameter("@displayName", SqlDbType.NVarChar, 255) { Value = (object?)NormalizeText(request.DisplayName, 255) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@categoryCode", SqlDbType.NVarChar, 100) { Value = (object?)NormalizeText(request.CategoryCode, 100) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@categoryTitle", SqlDbType.NVarChar, 255) { Value = (object?)NormalizeText(request.CategoryTitle, 255) ?? DBNull.Value });
        AddNullableBit(cmd, "@canTraverse", request.CanTraverse);
        AddNullableBit(cmd, "@showInCard", request.ShowInCard);
        AddNullableBit(cmd, "@showInTree", request.ShowInTree);
        AddNullableInt(cmd, "@maxDepth", request.MaxDepth is null ? null : Math.Clamp(request.MaxDepth.Value, 0, 3));
        AddNullableInt(cmd, "@maxItems", request.MaxItems is null ? null : Math.Clamp(request.MaxItems.Value, 1, 5000));
        cmd.Parameters.Add(new SqlParameter("@directionMode", SqlDbType.NVarChar, 20) { Value = (object?)NormalizeDirectionMode(request.DirectionMode) ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);

        return await GetRelationAsync(user, sourceCode, roleCode, relationGroupId, ct)
            ?? throw new InvalidOperationException("Relation policy was saved, but resulting row was not found.");
    }

    private async Task<IReadOnlyList<ReporterAdminGroupPolicyDto>> ReadGroupsAsync(
        SqlConnection cn,
        string sourceCode,
        int sourceId,
        string roleCode,
        int roleId,
        int? groupId,
        string? query,
        bool enabledOnly,
        int take,
        CancellationToken ct)
    {
        take = ClampTake(take);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (@take)
    g.GroupId,
    TRY_CONVERT(nvarchar(36), g.GroupGuid) AS GroupGuid,
    g.TableName,
    g.Caption,
    g.[Type],
    g.VisibleInTflex,
    p.SourceId AS PolicySourceId,
    p.IsEnabled,
    p.CanSeeInMenu,
    p.CanSearch,
    p.CanOpenCard,
    p.CanExport,
    p.MaxObjectDepth,
    p.MaxFileTreeDepth,
    p.MaxRowsPerPage,
    p.MaxRelatedObjects
FROM app.TflexGroup g
OUTER APPLY (
    SELECT TOP (1) p.*
    FROM app.GroupAccessPolicy p
    WHERE p.GroupId = g.GroupId
      AND p.AppRoleId = @roleId
      AND (p.SourceId = @sourceId OR p.SourceId IS NULL)
    ORDER BY CASE WHEN p.SourceId = @sourceId THEN 0 ELSE 1 END
) p
WHERE g.SourceId = @sourceId
  AND (@groupId IS NULL OR g.GroupId = @groupId)
  AND (
        @query IS NULL OR @query = N''
        OR g.TableName LIKE N'%' + @query + N'%'
        OR g.Caption LIKE N'%' + @query + N'%'
        OR TRY_CONVERT(nvarchar(32), g.GroupId) = @query
      )
  AND (
        @enabledOnly = 0
        OR (p.IsEnabled = 1 AND (p.CanSeeInMenu = 1 OR p.CanSearch = 1 OR p.CanOpenCard = 1 OR p.CanExport = 1))
      )
ORDER BY
    CASE WHEN p.IsEnabled = 1 THEN 0 ELSE 1 END,
    CASE WHEN p.CanSeeInMenu = 1 THEN 0 ELSE 1 END,
    COALESCE(g.Caption, g.TableName),
    g.GroupId;";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@roleId", SqlDbType.Int) { Value = roleId });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = (object?)groupId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@query", SqlDbType.NVarChar, 255) { Value = (object?)NormalizeText(query, 255) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@enabledOnly", SqlDbType.Bit) { Value = enabledOnly });
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });

        var result = new List<ReporterAdminGroupPolicyDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var policySourceId = ReadNullableInt(reader, "PolicySourceId");
            result.Add(new ReporterAdminGroupPolicyDto(
                sourceCode,
                ReadInt(reader, "GroupId"),
                reader.GetNullableString("GroupGuid"),
                reader.GetString(reader.GetOrdinal("TableName")),
                reader.GetNullableString("Caption"),
                ReadInt(reader, "Type"),
                ReadNullableBool(reader, "VisibleInTflex"),
                roleCode,
                policySourceId is not null,
                policySourceId == sourceId,
                ReadBoolOrDefault(reader, "IsEnabled", false),
                ReadBoolOrDefault(reader, "CanSeeInMenu", false),
                ReadBoolOrDefault(reader, "CanSearch", false),
                ReadBoolOrDefault(reader, "CanOpenCard", false),
                ReadBoolOrDefault(reader, "CanExport", false),
                ReadIntOrDefault(reader, "MaxObjectDepth", 0),
                ReadIntOrDefault(reader, "MaxFileTreeDepth", 0),
                ReadIntOrDefault(reader, "MaxRowsPerPage", 0),
                ReadIntOrDefault(reader, "MaxRelatedObjects", 0)));
        }

        return result;
    }

    private async Task<IReadOnlyList<ReporterAdminRelationPolicyDto>> ReadRelationsAsync(
        SqlConnection cn,
        string sourceCode,
        int sourceId,
        string roleCode,
        int roleId,
        int? relationGroupId,
        int? groupId,
        string? query,
        bool enabledOnly,
        int take,
        CancellationToken ct)
    {
        take = ClampTake(take);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (@take)
    r.RelationGroupId,
    TRY_CONVERT(nvarchar(36), r.RelationGuid) AS RelationGuid,
    r.RelationTable,
    r.RelationCaption,
    r.MasterGroupId,
    mg.TableName AS MasterTableName,
    mg.Caption AS MasterCaption,
    r.SlaveGroupId,
    sg.TableName AS SlaveTableName,
    sg.Caption AS SlaveCaption,
    p.SourceId AS PolicySourceId,
    p.IsEnabled,
    p.DisplayName,
    p.CategoryCode,
    p.CategoryTitle,
    p.CanTraverse,
    p.ShowInCard,
    p.ShowInTree,
    p.MaxDepth,
    p.MaxItems,
    p.DirectionMode
FROM app.TflexRelation r
LEFT JOIN app.TflexGroup mg ON mg.SourceId = r.SourceId AND mg.GroupId = r.MasterGroupId
LEFT JOIN app.TflexGroup sg ON sg.SourceId = r.SourceId AND sg.GroupId = r.SlaveGroupId
OUTER APPLY (
    SELECT TOP (1) p.*
    FROM app.RelationAccessPolicy p
    WHERE p.RelationTable = r.RelationTable
      AND p.AppRoleId = @roleId
      AND (p.SourceId = @sourceId OR p.SourceId IS NULL)
    ORDER BY CASE WHEN p.SourceId = @sourceId THEN 0 ELSE 1 END
) p
WHERE r.SourceId = @sourceId
  AND (@relationGroupId IS NULL OR r.RelationGroupId = @relationGroupId)
  AND (@groupId IS NULL OR r.MasterGroupId = @groupId OR r.SlaveGroupId = @groupId)
  AND (
        @query IS NULL OR @query = N''
        OR r.RelationTable LIKE N'%' + @query + N'%'
        OR r.RelationCaption LIKE N'%' + @query + N'%'
        OR mg.TableName LIKE N'%' + @query + N'%'
        OR sg.TableName LIKE N'%' + @query + N'%'
        OR TRY_CONVERT(nvarchar(32), r.RelationGroupId) = @query
      )
  AND (
        @enabledOnly = 0
        OR (p.IsEnabled = 1 AND p.CanTraverse = 1)
      )
ORDER BY
    CASE WHEN p.IsEnabled = 1 THEN 0 ELSE 1 END,
    CASE WHEN p.ShowInTree = 1 THEN 0 ELSE 1 END,
    COALESCE(p.CategoryTitle, p.DisplayName, r.RelationCaption, r.RelationTable),
    r.RelationGroupId;";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@roleId", SqlDbType.Int) { Value = roleId });
        cmd.Parameters.Add(new SqlParameter("@relationGroupId", SqlDbType.Int) { Value = (object?)relationGroupId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = (object?)groupId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@query", SqlDbType.NVarChar, 255) { Value = (object?)NormalizeText(query, 255) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@enabledOnly", SqlDbType.Bit) { Value = enabledOnly });
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });

        var result = new List<ReporterAdminRelationPolicyDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var policySourceId = ReadNullableInt(reader, "PolicySourceId");
            result.Add(new ReporterAdminRelationPolicyDto(
                sourceCode,
                ReadInt(reader, "RelationGroupId"),
                reader.GetNullableString("RelationGuid"),
                reader.GetString(reader.GetOrdinal("RelationTable")),
                reader.GetNullableString("RelationCaption"),
                ReadNullableInt(reader, "MasterGroupId"),
                reader.GetNullableString("MasterTableName"),
                reader.GetNullableString("MasterCaption"),
                ReadNullableInt(reader, "SlaveGroupId"),
                reader.GetNullableString("SlaveTableName"),
                reader.GetNullableString("SlaveCaption"),
                roleCode,
                policySourceId is not null,
                policySourceId == sourceId,
                ReadBoolOrDefault(reader, "IsEnabled", false),
                reader.GetNullableString("DisplayName"),
                reader.GetNullableString("CategoryCode"),
                reader.GetNullableString("CategoryTitle"),
                ReadBoolOrDefault(reader, "CanTraverse", false),
                ReadBoolOrDefault(reader, "ShowInCard", false),
                ReadBoolOrDefault(reader, "ShowInTree", false),
                ReadIntOrDefault(reader, "MaxDepth", 0),
                ReadIntOrDefault(reader, "MaxItems", 0),
                reader.GetNullableString("DirectionMode") ?? "Both"));
        }

        return result;
    }

    private async Task<int> GetSourceIdAsync(SqlConnection cn, string sourceCode, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT SourceId FROM app.Source WHERE Code = @code AND IsEnabled = 1";
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 100) { Value = sourceCode });
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null || value is DBNull
            ? throw new InvalidOperationException($"Source '{sourceCode}' not found or disabled in app.Source.")
            : Convert.ToInt32(value);
    }

    private async Task<int> GetRoleIdAsync(SqlConnection cn, string roleCode, CancellationToken ct)
    {
        roleCode = string.IsNullOrWhiteSpace(roleCode) ? _options.DefaultRoleCode : roleCode.Trim();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT AppRoleId FROM app.AppRole WHERE Code = @code";
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 100) { Value = roleCode });
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null || value is DBNull
            ? throw new InvalidOperationException($"Role '{roleCode}' not found in app.AppRole.")
            : Convert.ToInt32(value);
    }

    private async Task<string> GetGroupTableNameAsync(SqlConnection cn, int sourceId, int groupId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT TableName FROM app.TflexGroup WHERE SourceId = @sourceId AND GroupId = @groupId";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });
        var value = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"TflexGroup {groupId} was not found in source {sourceId}.")
            : value;
    }

    private async Task<RelationRow> GetRelationRowAsync(SqlConnection cn, int sourceId, int relationGroupId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT RelationGroupId, RelationTable, RelationCaption
FROM app.TflexRelation
WHERE SourceId = @sourceId AND RelationGroupId = @relationGroupId;";
        cmd.Parameters.Add(new SqlParameter("@sourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@relationGroupId", SqlDbType.Int) { Value = relationGroupId });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"TflexRelation {relationGroupId} was not found in source {sourceId}.");

        return new RelationRow(
            ReadInt(reader, "RelationGroupId"),
            reader.GetString(reader.GetOrdinal("RelationTable")),
            reader.GetNullableString("RelationCaption"));
    }

    private int ClampTake(int take) =>
        Math.Clamp(take <= 0 ? _options.AdminDefaultTake : take, 1, 1000);

    private static string? NormalizeText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? NormalizeDirectionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.Equals("MasterToSlave", StringComparison.OrdinalIgnoreCase))
            return "MasterToSlave";
        if (value.Equals("SlaveToMaster", StringComparison.OrdinalIgnoreCase))
            return "SlaveToMaster";
        return "Both";
    }

    private static void AddNullableBit(SqlCommand cmd, string name, bool? value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Bit) { Value = (object?)value ?? DBNull.Value });

    private static void AddNullableInt(SqlCommand cmd, string name, int? value) =>
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Int) { Value = (object?)value ?? DBNull.Value });

    private static int ReadInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static int ReadIntOrDefault(SqlDataReader reader, string name, int fallback)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? fallback : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static int? ReadNullableInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static bool ReadBool(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal));
    }

    private static bool ReadBoolOrDefault(SqlDataReader reader, string name, bool fallback)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? fallback : Convert.ToBoolean(reader.GetValue(ordinal));
    }

    private static bool? ReadNullableBool(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToBoolean(reader.GetValue(ordinal));
    }

    private sealed record RelationRow(int RelationGroupId, string RelationTable, string? RelationCaption);
}
