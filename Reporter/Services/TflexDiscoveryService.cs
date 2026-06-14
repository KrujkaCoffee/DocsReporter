using System.Data;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using Microsoft.Data.SqlClient;

namespace DocsApi.Reporter.Services;

public interface ITflexDiscoveryService
{
    Task<DiscoveryResultDto> DiscoverParameterGroupsAsync(string sourceCode, CancellationToken ct);
    Task<BootstrapResultDto> BootstrapProjectCardViewerAsync(string sourceCode, CancellationToken ct);
}

public sealed class TflexDiscoveryService : ITflexDiscoveryService
{
    private readonly IReporterSqlConnectionFactory _factory;

    public TflexDiscoveryService(IReporterSqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<DiscoveryResultDto> DiscoverParameterGroupsAsync(string sourceCode, CancellationToken ct)
    {
        var sourceId = await GetSourceIdAsync(sourceCode, ct);
        var groups = new List<TflexGroupRow>();
        var relations = new List<TflexRelationRow>();

        await using (var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct))
        {
            await using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
    pg.PK AS GroupId,
    TRY_CONVERT(uniqueidentifier, pg.Guid) AS GroupGuid,
    pg.TableName,
    pg.Caption,
    pg.[Type],
    pg.DefaultParameterID,
    pg.HierarchyType,
    pg.Visible
FROM dbo.ParameterGroups pg
WHERE pg.TableName IS NOT NULL;";

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    groups.Add(new TflexGroupRow(
                        r.GetInt32Flexible("GroupId"),
                        r.IsDBNull(r.GetOrdinal("GroupGuid")) ? null : r.GetGuid(r.GetOrdinal("GroupGuid")),
                        r.GetString(r.GetOrdinal("TableName")),
                        r.IsDBNull(r.GetOrdinal("Caption")) ? null : r.GetString(r.GetOrdinal("Caption")),
                        r.GetInt32Flexible("Type"),
                        r.IsDBNull(r.GetOrdinal("DefaultParameterID")) ? null : r.GetInt32Flexible("DefaultParameterID"),
                        r.IsDBNull(r.GetOrdinal("HierarchyType")) ? null : r.GetInt32Flexible("HierarchyType"),
                        //r.IsDBNull(r.GetOrdinal("Visible")) ? null : r.GetBoolean(r.GetOrdinal("Visible"))));
                    ReadNullableBool(r, "Visible")));
                }
            }

            await using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
    l.PK AS RelationGroupId,
    TRY_CONVERT(uniqueidentifier, l.Guid) AS RelationGuid,
    l.TableName AS RelationTable,
    l.Caption AS RelationCaption,
    l.MasterGroupID,
    l.SlaveGroupID,
    l.LinkType,
    l.LinkVisibility,
    l.DoubleDirectionLink,
    l.IsAsymmetricLink,
    l.LinkRequired
FROM dbo.ParameterGroups l
WHERE l.[Type] = 5
  AND l.TableName IS NOT NULL;";

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    relations.Add(new TflexRelationRow(
                        r.GetInt32Flexible("RelationGroupId"),
                        r.IsDBNull(r.GetOrdinal("RelationGuid")) ? null : r.GetGuid(r.GetOrdinal("RelationGuid")),
                        r.GetString(r.GetOrdinal("RelationTable")),
                        r.IsDBNull(r.GetOrdinal("RelationCaption")) ? null : r.GetString(r.GetOrdinal("RelationCaption")),
                        r.IsDBNull(r.GetOrdinal("MasterGroupID")) ? null : r.GetInt32Flexible("MasterGroupID"),
                        r.IsDBNull(r.GetOrdinal("SlaveGroupID")) ? null : r.GetInt32Flexible("SlaveGroupID"),
                        r.IsDBNull(r.GetOrdinal("LinkType")) ? null : r.GetInt32Flexible("LinkType"),
                        r.IsDBNull(r.GetOrdinal("LinkVisibility")) ? null : r.GetInt32Flexible("LinkVisibility"),
//r.IsDBNull(r.GetOrdinal("DoubleDirectionLink")) ? null : r.GetBoolean(r.GetOrdinal("DoubleDirectionLink")),
//r.IsDBNull(r.GetOrdinal("IsAsymmetricLink")) ? null : r.GetBoolean(r.GetOrdinal("IsAsymmetricLink")),
//r.IsDBNull(r.GetOrdinal("LinkRequired")) ? null : r.GetBoolean(r.GetOrdinal("LinkRequired"))));
ReadNullableBool(r, "DoubleDirectionLink"),
ReadNullableBool(r, "IsAsymmetricLink"),
ReadNullableBool(r, "LinkRequired")));
                }
            }
        }

        await using (var app = await _factory.OpenAppConnectionAsync(ct))
        {
            foreach (var g in groups)
                await UpsertGroupAsync(app, sourceId, g, ct);

            foreach (var rel in relations)
                await UpsertRelationAsync(app, sourceId, rel, ct);
        }

        return new DiscoveryResultDto(sourceCode, groups.Count, relations.Count);
    }

    public async Task<BootstrapResultDto> BootstrapProjectCardViewerAsync(string sourceCode, CancellationToken ct)
    {
        var sourceId = await GetSourceIdAsync(sourceCode, ct);
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
DECLARE @RoleId int = (SELECT AppRoleId FROM app.AppRole WHERE Code = N'ProjectCardViewer');
IF @RoleId IS NULL THROW 50000, 'Role ProjectCardViewer not found', 1;

DECLARE @GroupPolicies table(x int);
DECLARE @RelationPolicies table(x int);

MERGE app.GroupAccessPolicy AS target
USING (
    SELECT @SourceId AS SourceId, @RoleId AS AppRoleId, GroupId, TableName,
           CASE WHEN TableName = N'Kartochka_proekta' THEN 1 ELSE 0 END AS CanSeeInMenu,
           CASE WHEN TableName IN (N'Kartochka_proekta', N'Nomenclature') THEN 1 ELSE 0 END AS CanSearch,
           1 AS CanOpenCard,
           0 AS CanExport,
           2 AS MaxObjectDepth,
           4 AS MaxFileTreeDepth,
           100 AS MaxRowsPerPage,
           500 AS MaxRelatedObjects,
           1 AS IsEnabled
    FROM app.TflexGroup
    WHERE SourceId = @SourceId
      AND TableName IN (N'Kartochka_proekta', N'Nomenclature', N'Documents', N'Files')
) AS src
ON target.SourceId = src.SourceId AND target.AppRoleId = src.AppRoleId AND target.GroupId = src.GroupId
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
    src.MaxObjectDepth, src.MaxFileTreeDepth, src.MaxRowsPerPage, src.MaxRelatedObjects, src.IsEnabled)
OUTPUT 1 INTO @GroupPolicies;

MERGE app.RelationAccessPolicy AS target
USING (
    SELECT
        @SourceId AS SourceId,
        @RoleId AS AppRoleId,
        r.RelationGroupId,
        r.RelationTable,
        r.RelationCaption AS DisplayName,
        CASE r.RelationTable
            WHEN N'Papka_TZ_kartochka_pr_1' THEN N'tz'
            WHEN N'Tekhnicheskoe_zadanie_Kar' THEN N'tz_object'
            WHEN N'Papka_TZ_Tekhnicheskoe_za' THEN N'tz'
            WHEN N'Link_899_16' THEN N'tz'
            WHEN N'Link_891_403_1' THEN N'nomenclature'
            WHEN N'Link_403_891_1' THEN N'nomenclature'
            WHEN N'Link_891_403' THEN N'nomenclature'
            WHEN N'Link_891_16' THEN N'vo_cad'
            WHEN N'Link_891_16_1' THEN N'vo_pdf'
            WHEN N'Link_891_16_2' THEN N'root'
            WHEN N'Link_891_16_3' THEN N'doc'
            WHEN N'Link_891_16_4' THEN N'control'
            WHEN N'Link_891_16_5' THEN N'tests'
            WHEN N'Link_891_1802' THEN N'documents'
            WHEN N'Link_1802_16_1' THEN N'documents_root'
            WHEN N'Link_1802_16_2' THEN N'documents_files'
            WHEN N'DocumentFiles' THEN N'documents_files'
            WHEN N'Link_Documents_Files' THEN N'documents_files'
            WHEN N'Link_Documents_Files_1' THEN N'documents_files'
            ELSE N'relation'
        END AS CategoryCode,
        CASE r.RelationTable
            WHEN N'Papka_TZ_kartochka_pr_1' THEN N'ТЗ'
            WHEN N'Tekhnicheskoe_zadanie_Kar' THEN N'ТЗ'
            WHEN N'Papka_TZ_Tekhnicheskoe_za' THEN N'ТЗ'
            WHEN N'Link_899_16' THEN N'ТЗ'
            WHEN N'Link_891_403_1' THEN N'Изделие проекта'
            WHEN N'Link_403_891_1' THEN N'Изделие проекта'
            WHEN N'Link_891_403' THEN N'Изделие проекта'
            WHEN N'Link_891_16' THEN N'ВО CAD'
            WHEN N'Link_891_16_1' THEN N'ВО PDF'
            WHEN N'Link_891_16_2' THEN N'Головная папка'
            WHEN N'Link_891_16_3' THEN N'Док'
            WHEN N'Link_891_16_4' THEN N'Контроль'
            WHEN N'Link_891_16_5' THEN N'Испытания'
            WHEN N'Link_891_1802' THEN N'Документы КП'
            WHEN N'Link_1802_16_1' THEN N'Документы КП'
            WHEN N'Link_1802_16_2' THEN N'Документы КП'
            WHEN N'DocumentFiles' THEN N'Файлы документов'
            WHEN N'Link_Documents_Files' THEN N'Файлы документов'
            WHEN N'Link_Documents_Files_1' THEN N'Файлы документов'
            ELSE r.RelationCaption
        END AS CategoryTitle,
        1 AS CanTraverse,
        1 AS ShowInCard,
        CASE WHEN r.RelationTable IN (
            N'Papka_TZ_kartochka_pr_1', N'Papka_TZ_Tekhnicheskoe_za', N'Link_899_16', N'Link_891_16', N'Link_891_16_1',
            N'Link_891_16_2', N'Link_891_16_3', N'Link_891_16_4', N'Link_891_16_5',
            N'Link_1802_16_1', N'Link_1802_16_2', N'DocumentFiles', N'Link_Documents_Files', N'Link_Documents_Files_1'
        ) THEN 1 ELSE 0 END AS ShowInTree,
        1 AS MaxDepth,
        500 AS MaxItems,
        N'Both' AS DirectionMode,
        1 AS IsEnabled
    FROM app.TflexRelation r
    WHERE r.SourceId = @SourceId
      AND r.RelationTable IN (
        N'Link_891_403_1', N'Link_403_891_1', N'Link_891_403',
        N'Papka_TZ_kartochka_pr_1', N'Tekhnicheskoe_zadanie_Kar', N'Papka_TZ_Tekhnicheskoe_za', N'Link_899_16',
        N'Link_891_16', N'Link_891_16_1', N'Link_891_16_2', N'Link_891_16_3', N'Link_891_16_4', N'Link_891_16_5',
        N'Link_891_1802', N'Link_1802_16_1', N'Link_1802_16_2',
        N'DocumentFiles', N'Link_Documents_Files', N'Link_Documents_Files_1'
      )
) AS src
ON target.SourceId = src.SourceId AND target.AppRoleId = src.AppRoleId AND target.RelationTable = src.RelationTable
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
    src.CanTraverse, src.ShowInCard, src.ShowInTree, src.MaxDepth, src.MaxItems, src.DirectionMode, src.IsEnabled)
OUTPUT 1 INTO @RelationPolicies;

SELECT
    (SELECT COUNT(*) FROM @GroupPolicies) AS GroupPolicyCount,
    (SELECT COUNT(*) FROM @RelationPolicies) AS RelationPolicyCount;";
        cmd.Parameters.Add(new SqlParameter("@SourceId", SqlDbType.Int) { Value = sourceId });

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new BootstrapResultDto(sourceCode, r.GetInt32(0), r.GetInt32(1));
    }

    private async Task<int> GetSourceIdAsync(string sourceCode, CancellationToken ct)
    {
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = "SELECT SourceId FROM app.Source WHERE Code = @code AND IsEnabled = 1";
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 100) { Value = sourceCode });
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null || value is DBNull
            ? throw new InvalidOperationException($"Source '{sourceCode}' not found or disabled in app.Source.")
            : Convert.ToInt32(value);
    }

    private static async Task UpsertGroupAsync(SqlConnection app, int sourceId, TflexGroupRow row, CancellationToken ct)
    {
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
MERGE app.TflexGroup AS target
USING (SELECT @SourceId SourceId, @GroupId GroupId) AS src
ON target.SourceId = src.SourceId AND target.GroupId = src.GroupId
WHEN MATCHED THEN UPDATE SET
    GroupGuid = @GroupGuid,
    TableName = @TableName,
    Caption = @Caption,
    [Type] = @Type,
    DefaultParameterId = @DefaultParameterId,
    HierarchyType = @HierarchyType,
    VisibleInTflex = @VisibleInTflex,
    DiscoveredAt = sysdatetime()
WHEN NOT MATCHED THEN INSERT(SourceId, GroupId, GroupGuid, TableName, Caption, [Type], DefaultParameterId, HierarchyType, VisibleInTflex)
VALUES(@SourceId, @GroupId, @GroupGuid, @TableName, @Caption, @Type, @DefaultParameterId, @HierarchyType, @VisibleInTflex);";
        cmd.Parameters.Add(new SqlParameter("@SourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@GroupId", SqlDbType.Int) { Value = row.GroupId });
        cmd.Parameters.Add(new SqlParameter("@GroupGuid", SqlDbType.UniqueIdentifier) { Value = (object?)row.GroupGuid ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@TableName", SqlDbType.NVarChar, 128) { Value = row.TableName });
        cmd.Parameters.Add(new SqlParameter("@Caption", SqlDbType.NVarChar, 255) { Value = (object?)row.Caption ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@Type", SqlDbType.Int) { Value = row.Type });
        cmd.Parameters.Add(new SqlParameter("@DefaultParameterId", SqlDbType.Int) { Value = (object?)row.DefaultParameterId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@HierarchyType", SqlDbType.Int) { Value = (object?)row.HierarchyType ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@VisibleInTflex", SqlDbType.Bit) { Value = (object?)row.Visible ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertRelationAsync(SqlConnection app, int sourceId, TflexRelationRow row, CancellationToken ct)
    {
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
MERGE app.TflexRelation AS target
USING (SELECT @SourceId SourceId, @RelationGroupId RelationGroupId) AS src
ON target.SourceId = src.SourceId AND target.RelationGroupId = src.RelationGroupId
WHEN MATCHED THEN UPDATE SET
    RelationGuid = @RelationGuid,
    RelationTable = @RelationTable,
    RelationCaption = @RelationCaption,
    MasterGroupId = @MasterGroupId,
    SlaveGroupId = @SlaveGroupId,
    LinkType = @LinkType,
    LinkVisibility = @LinkVisibility,
    DoubleDirectionLink = @DoubleDirectionLink,
    IsAsymmetricLink = @IsAsymmetricLink,
    LinkRequired = @LinkRequired,
    DiscoveredAt = sysdatetime()
WHEN NOT MATCHED THEN INSERT(SourceId, RelationGroupId, RelationGuid, RelationTable, RelationCaption, MasterGroupId, SlaveGroupId, LinkType, LinkVisibility, DoubleDirectionLink, IsAsymmetricLink, LinkRequired)
VALUES(@SourceId, @RelationGroupId, @RelationGuid, @RelationTable, @RelationCaption, @MasterGroupId, @SlaveGroupId, @LinkType, @LinkVisibility, @DoubleDirectionLink, @IsAsymmetricLink, @LinkRequired);";
        cmd.Parameters.Add(new SqlParameter("@SourceId", SqlDbType.Int) { Value = sourceId });
        cmd.Parameters.Add(new SqlParameter("@RelationGroupId", SqlDbType.Int) { Value = row.RelationGroupId });
        cmd.Parameters.Add(new SqlParameter("@RelationGuid", SqlDbType.UniqueIdentifier) { Value = (object?)row.RelationGuid ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@RelationTable", SqlDbType.NVarChar, 128) { Value = row.RelationTable });
        cmd.Parameters.Add(new SqlParameter("@RelationCaption", SqlDbType.NVarChar, 255) { Value = (object?)row.RelationCaption ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@MasterGroupId", SqlDbType.Int) { Value = (object?)row.MasterGroupId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@SlaveGroupId", SqlDbType.Int) { Value = (object?)row.SlaveGroupId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@LinkType", SqlDbType.Int) { Value = (object?)row.LinkType ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@LinkVisibility", SqlDbType.Int) { Value = (object?)row.LinkVisibility ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@DoubleDirectionLink", SqlDbType.Bit) { Value = (object?)row.DoubleDirectionLink ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@IsAsymmetricLink", SqlDbType.Bit) { Value = (object?)row.IsAsymmetricLink ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@LinkRequired", SqlDbType.Bit) { Value = (object?)row.LinkRequired ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static bool? ReadNullableBool(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
            return null;

        object value = reader.GetValue(ordinal);

        return value switch
        {
            bool b => b,
            byte b => b != 0,
            short s => s != 0,
            int i => i != 0,
            long l => l != 0,
            decimal d => d != 0,
            string s when bool.TryParse(s, out bool b) => b,
            string s when int.TryParse(s, out int i) => i != 0,
            _ => Convert.ToBoolean(value)
        };
    }

    private sealed record TflexGroupRow(int GroupId, Guid? GroupGuid, string TableName, string? Caption, int Type, int? DefaultParameterId, int? HierarchyType, bool? Visible);
    private sealed record TflexRelationRow(int RelationGroupId, Guid? RelationGuid, string RelationTable, string? RelationCaption, int? MasterGroupId, int? SlaveGroupId, int? LinkType, int? LinkVisibility, bool? DoubleDirectionLink, bool? IsAsymmetricLink, bool? LinkRequired);
}
