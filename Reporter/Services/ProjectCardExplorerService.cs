using System.Data;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using Microsoft.Data.SqlClient;

namespace DocsApi.Reporter.Services;

public interface IProjectCardExplorerService
{
    Task<IReadOnlyList<ProjectCardSearchItemDto>> SearchAsync(ClaimsPrincipal user, string sourceCode, string? query, int page, int pageSize, CancellationToken ct);
    Task<ProjectCardFullDto?> GetFullCardAsync(ClaimsPrincipal user, string sourceCode, long objectId, int requestedDepth, int requestedFileDepth, CancellationToken ct);
}

public sealed class ProjectCardExplorerService : IProjectCardExplorerService
{
    private const int ProjectCardGroupId = 891;
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _access;
    private readonly IProjectCardFileExplorerService _files;

    public ProjectCardExplorerService(
        IReporterSqlConnectionFactory factory,
        IReporterAccessService access,
        IProjectCardFileExplorerService files)
    {
        _factory = factory;
        _access = access;
        _files = files;
    }

    public async Task<IReadOnlyList<ProjectCardSearchItemDto>> SearchAsync(
        ClaimsPrincipal user,
        string sourceCode,
        string? query,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        //var access = await _access.GetGroupAccessAsync(user, sourceCode, ProjectCardGroupId, ct);
        //if (access is null || !access.CanSearch)
        //    throw new UnauthorizedAccessException("No access to search project cards.");

        page = Math.Max(1, page);
        //pageSize = Math.Clamp(pageSize, 1, Math.Min(access.MaxRowsPerPage, 500));
        pageSize = Math.Clamp(pageSize, 1, Math.Min(3, 500));
        var offset = (page - 1) * pageSize;

        await using var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        var columns = await SqlHelpers.GetColumnsAsync(src, "dbo", "Kartochka_proekta", ct);
        var codeCol = SqlHelpers.FirstExisting(columns, "Obekt", "Obect", "Object", "Shifr_izdeliya", "Code", "Designation");
        var nameCol = SqlHelpers.FirstExisting(columns, "Name", "Naimenovanie", "Nazvanie");
        var guidCol = columns.Contains("s_Guid") ? "s_Guid" : null;

        if (codeCol is null && nameCol is null)
            throw new InvalidOperationException("Kartochka_proekta has no display columns among Obekt/Obect/Name.");

        var selectCode = codeCol is null ? "CAST(NULL AS nvarchar(4000))" : $"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(codeCol)})";
        var selectName = nameCol is null ? "CAST(NULL AS nvarchar(4000))" : $"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(nameCol)})";
        var selectGuid = guidCol is null ? "CAST(NULL AS nvarchar(36))" : $"TRY_CONVERT(nvarchar(36), {SqlHelpers.QuoteName(guidCol)})";

        var predicates = new List<string>();
        if (codeCol is not null) predicates.Add($"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(codeCol)}) LIKE N'%' + @query + N'%'");
        if (nameCol is not null) predicates.Add($"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(nameCol)}) LIKE N'%' + @query + N'%'");
        var searchPredicate = predicates.Count == 0 ? "1 = 1" : string.Join(" OR ", predicates);

        await using var cmd = src.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT
    s_ObjectID AS ObjectId,
    {selectGuid} AS Guid,
    {selectCode} AS ObjectCode,
    {selectName} AS [Name]
FROM dbo.Kartochka_proekta
WHERE s_ActualVersion = 1
  AND s_Deleted = 0
  AND (@query IS NULL OR @query = N'' OR ({searchPredicate}))
ORDER BY s_ObjectID DESC
OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";
        cmd.Parameters.Add(new SqlParameter("@query", SqlDbType.NVarChar, 4000) { Value = (object?)query ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
        cmd.Parameters.Add(new SqlParameter("@pageSize", SqlDbType.Int) { Value = pageSize });

        var result = new List<ProjectCardSearchItemDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var objectId = Convert.ToInt64(r["ObjectId"]);
            var guid = r.GetNullableString("Guid");
            result.Add(new ProjectCardSearchItemDto(
                sourceCode,
                objectId,
                guid,
                r.GetNullableString("ObjectCode"),
                r.GetNullableString("Name"),
                await BuildDocsUrlAsync(sourceCode, ProjectCardGroupId, objectId, ct)));
        }
        return result;
    }

    public async Task<ProjectCardFullDto?> GetFullCardAsync(
        ClaimsPrincipal user,
        string sourceCode,
        long objectId,
        int requestedDepth,
        int requestedFileDepth,
        CancellationToken ct)
    {
        //var groupAccess = await _access.GetGroupAccessAsync(user, sourceCode, ProjectCardGroupId, ct);
        //if (groupAccess is null || !groupAccess.CanOpenCard)
        //    throw new UnauthorizedAccessException("No access to open project card.");

        var effectiveDepth = Math.Clamp(Math.Min(requestedDepth, 3), 0, 3);
        var effectiveFileDepth = Math.Clamp(Math.Min(requestedFileDepth, 4), 0, 6);
        //var effectiveDepth = Math.Clamp(Math.Min(requestedDepth, groupAccess.MaxObjectDepth), 0, 3);
        //var effectiveFileDepth = Math.Clamp(Math.Min(requestedFileDepth, groupAccess.MaxFileTreeDepth), 0, 6);
        //var maxRelated = groupAccess.MaxRelatedObjects;
        var maxRelated = 500;

        await using var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        var card = await ReadObjectPreviewAsync(sourceCode, src, ProjectCardGroupId, "Kartochka_proekta", objectId, ct);
        if (card is null)
            return null;

        var relations = new List<RelationItemDto>();
        if (effectiveDepth > 0)
        {
            var allowedRelations = await _access.GetAllowedRelationsAsync(user, sourceCode, ProjectCardGroupId, ct);
            foreach (var relAccess in allowedRelations)
            {
                var relMeta = await GetRelationMetaAsync(sourceCode, relAccess.RelationTable, ct);
                if (relMeta is null)
                    continue;

                var exists = await SqlHelpers.TableExistsAsync(src, "dbo", relAccess.RelationTable, ct);
                if (!exists)
                    continue;

                var maxItems = Math.Min(relAccess.MaxItems, maxRelated);
                var sql = $@"
SELECT TOP (@maxItems)
    MasterID,
    SlaveID
FROM dbo.{SqlHelpers.QuoteName(relAccess.RelationTable)}
WHERE MasterID = @objectId OR SlaveID = @objectId;";

                await using var cmd = src.CreateCommand();
                cmd.CommandTimeout = 30;
                cmd.CommandText = sql;
                cmd.Parameters.Add(new SqlParameter("@objectId", SqlDbType.BigInt) { Value = objectId });
                cmd.Parameters.Add(new SqlParameter("@maxItems", SqlDbType.Int) { Value = maxItems });

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var masterId = Convert.ToInt64(r["MasterID"]);
                    var slaveId = Convert.ToInt64(r["SlaveID"]);
                    var direction = masterId == objectId ? "OUT" : "IN";
                    var targetId = direction == "OUT" ? slaveId : masterId;
                    var targetGroupId = direction == "OUT" ? relMeta.SlaveGroupId : relMeta.MasterGroupId;

                    ObjectPreviewDto? target = null;
                    if (effectiveDepth > 1 && targetGroupId is not null)
                    {
                        var targetTable = await GetGroupTableAsync(sourceCode, targetGroupId.Value, ct);
                        if (!string.IsNullOrWhiteSpace(targetTable))
                            target = await ReadObjectPreviewAsync(sourceCode, src, targetGroupId.Value, targetTable, targetId, ct);
                    }
                    else
                    {
                        target = new ObjectPreviewDto(targetGroupId, null, targetId, null, null, null, null);
                    }

                    relations.Add(new RelationItemDto(
                        relAccess.RelationTable,
                        relAccess.DisplayName ?? relMeta.RelationCaption,
                        relAccess.CategoryCode,
                        relAccess.CategoryTitle,
                        direction,
                        masterId,
                        slaveId,
                        target));
                }
            }
        }

        var fileCategories = await _files.GetFileCategoriesAsync(
            user,
            sourceCode,
            objectId,
            effectiveFileDepth,
            maxRelated,
            ct);

        return new ProjectCardFullDto(
            sourceCode,
            ProjectCardGroupId,
            card,
            relations,
            fileCategories,
            requestedDepth,
            effectiveDepth,
            requestedFileDepth,
            effectiveFileDepth,
            new Dictionary<string, object?>
            {
                ["projectCardGroupId"] = ProjectCardGroupId,
                ["projectCardTable"] = "Kartochka_proekta",
                ["relationCount"] = relations.Count,
                ["fileCategoryCount"] = fileCategories.Count,
                ["fileCount"] = fileCategories.Sum(x => x.FileCount)
            });
    }

    private async Task<ObjectPreviewDto?> ReadObjectPreviewAsync(string sourceCode, SqlConnection src, int groupId, string tableName, long objectId, CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(src, "dbo", tableName, ct))
            return null;

        var cols = await SqlHelpers.GetColumnsAsync(src, "dbo", tableName, ct);
        if (!cols.Contains("s_ObjectID"))
            return null;

        var guidCol = cols.Contains("s_Guid") ? "s_Guid" : null;
        var codeCol = SqlHelpers.FirstExisting(cols, "Obekt", "Obect", "Object", "Shifr_izdeliya", "Code", "Designation", "Number");
        var nameCol = SqlHelpers.FirstExisting(cols, "Name", "Naimenovanie", "Nazvanie", "FullName", "OriginalName", "FileName");

        var selectGuid = guidCol is null ? "CAST(NULL AS nvarchar(36))" : $"TRY_CONVERT(nvarchar(36), {SqlHelpers.QuoteName(guidCol)})";
        var selectCode = codeCol is null ? "CAST(NULL AS nvarchar(4000))" : $"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(codeCol)})";
        var selectName = nameCol is null ? "CAST(NULL AS nvarchar(4000))" : $"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(nameCol)})";

        var actualFilter = cols.Contains("s_ActualVersion") ? "AND s_ActualVersion = 1" : string.Empty;
        var deletedFilter = cols.Contains("s_Deleted") ? "AND s_Deleted = 0" : string.Empty;

        await using var cmd = src.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP (1)
    s_ObjectID AS ObjectId,
    {selectGuid} AS Guid,
    {selectCode} AS ObjectCode,
    {selectName} AS [Name]
FROM dbo.{SqlHelpers.QuoteName(tableName)}
WHERE s_ObjectID = @objectId
{actualFilter}
{deletedFilter};";
        cmd.Parameters.Add(new SqlParameter("@objectId", SqlDbType.BigInt) { Value = objectId });

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return new ObjectPreviewDto(groupId, tableName, objectId, null, null, null, await BuildDocsUrlAsync(sourceCode, groupId, objectId, ct));

        return new ObjectPreviewDto(
            groupId,
            tableName,
            objectId,
            r.GetNullableString("Guid"),
            r.GetNullableString("ObjectCode"),
            r.GetNullableString("Name"),
            await BuildDocsUrlAsync(sourceCode, groupId, objectId, ct));
    }

    private async Task<string?> BuildDocsUrlAsync(string sourceCode, int groupId, long objectId, CancellationToken ct)
    {
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = "SELECT BaseDocsUrl FROM app.Source WHERE Code = @code";
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 100) { Value = sourceCode });
        var baseUrl = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return baseUrl.TrimEnd('/') + $"/OpenPropertiesInNewWindow/?refId={groupId}&objId={objectId}";
    }

    private async Task<string?> GetGroupTableAsync(string sourceCode, int groupId, CancellationToken ct)
    {
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
SELECT g.TableName
FROM app.TflexGroup g
JOIN app.Source s ON s.SourceId = g.SourceId
WHERE s.Code = @sourceCode AND g.GroupId = @groupId;";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<RelationMeta?> GetRelationMetaAsync(string sourceCode, string relationTable, CancellationToken ct)
    {
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
SELECT r.RelationCaption, r.MasterGroupId, r.SlaveGroupId
FROM app.TflexRelation r
JOIN app.Source s ON s.SourceId = r.SourceId
WHERE s.Code = @sourceCode AND r.RelationTable = @relationTable;";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
        cmd.Parameters.Add(new SqlParameter("@relationTable", SqlDbType.NVarChar, 128) { Value = relationTable });

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RelationMeta(
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetInt32(1),
            r.IsDBNull(2) ? null : r.GetInt32(2));
    }

    private sealed record RelationMeta(string? RelationCaption, int? MasterGroupId, int? SlaveGroupId);
}
