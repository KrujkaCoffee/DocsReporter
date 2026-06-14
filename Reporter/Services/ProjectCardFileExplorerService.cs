using System.Data;
using System.Globalization;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using Microsoft.Data.SqlClient;

namespace DocsApi.Reporter.Services;

public interface IProjectCardFileExplorerService
{
    Task<IReadOnlyList<FileCategoryDto>> GetFileCategoriesAsync(
        ClaimsPrincipal user,
        string sourceCode,
        long projectCardObjectId,
        int requestedFileDepth,
        int maxItems,
        CancellationToken ct);
}

public sealed class ProjectCardFileExplorerService : IProjectCardFileExplorerService
{
    private const int ProjectCardGroupId = 891;

    private static readonly IReadOnlyList<DirectFileCategory> DirectFileCategories = new[]
    {
        new DirectFileCategory("vo_cad", "ВО CAD", "Link_891_16"),
        new DirectFileCategory("vo_pdf", "ВО PDF", "Link_891_16_1"),
        new DirectFileCategory("root", "Головная папка", "Link_891_16_2"),
        new DirectFileCategory("doc", "Док", "Link_891_16_3"),
        new DirectFileCategory("control", "Контроль", "Link_891_16_4"),
        new DirectFileCategory("tests", "Испытания", "Link_891_16_5"),
    };

    private static readonly string[] ProjectToNomenclatureRelations =
    {
        "Link_891_403_1",
        "Link_403_891_1",
        "Link_891_403"
    };

    private static readonly string[] ProjectToDocumentRelations =
    {
        "Link_891_1802"
    };

    private static readonly string[] TechnicalTaskToFileRelations =
    {
        "Papka_TZ_Tekhnicheskoe_za",
        "Link_899_16"
    };

    private static readonly string[] DocumentToFileRelations =
    {
        "Link_1802_16_1",
        "Link_1802_16_2",
        "DocumentFiles",
        "Link_Documents_Files",
        "Link_Documents_Files_1"
    };

    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _access;

    public ProjectCardFileExplorerService(IReporterSqlConnectionFactory factory, IReporterAccessService access)
    {
        _factory = factory;
        _access = access;
    }

    public async Task<IReadOnlyList<FileCategoryDto>> GetFileCategoriesAsync(
        ClaimsPrincipal user,
        string sourceCode,
        long projectCardObjectId,
        int requestedFileDepth,
        int maxItems,
        CancellationToken ct)
    {
        var effectiveFileDepth = Math.Clamp(requestedFileDepth, 0, 6);
        var effectiveMaxItems = Math.Clamp(maxItems, 1, 1000);

        var policies = await _access.GetAllowedFileRelationsAsync(user, sourceCode, ct);
        var policyByTable = policies
            .GroupBy(x => x.RelationTable, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        await using var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        if (!await SqlHelpers.TableExistsAsync(src, "dbo", "Files", ct))
            return Array.Empty<FileCategoryDto>();

        var fileSchema = await FileSchema.ReadAsync(src, ct);
        if (!fileSchema.HasObjectId)
            return Array.Empty<FileCategoryDto>();

        var result = new List<FileCategoryDto>();

        foreach (var directCategory in DirectFileCategories)
        {
            if (!policyByTable.TryGetValue(directCategory.RelationTable, out var policy))
                continue;

            var rootIds = await ReadTargetIdsFromRelationAsync(
                src,
                policy.RelationTable,
                projectCardObjectId,
                policy.DirectionMode,
                effectiveMaxItems,
                ct);

            var roots = await BuildFileTreeBatchAsync(
                sourceCode,
                src,
                fileSchema,
                rootIds,
                effectiveFileDepth,
                effectiveMaxItems,
                ct);

            AddCategoryIfNotEmpty(result, directCategory.Code, directCategory.Title, policy.RelationTable, roots);
        }

        var tzRoots = await ReadTechnicalTaskRootsAsync(
            sourceCode,
            src,
            fileSchema,
            policyByTable,
            projectCardObjectId,
            effectiveFileDepth,
            effectiveMaxItems,
            ct);
        AddCategoryIfNotEmpty(result, "tz", "ТЗ", "Tekhnicheskoe_zadanie_Kar/Papka_TZ_Tekhnicheskoe_za", tzRoots);

        var documentRoots = await ReadProjectDocumentRootsAsync(
            sourceCode,
            src,
            fileSchema,
            policyByTable,
            projectCardObjectId,
            effectiveFileDepth,
            effectiveMaxItems,
            ct);
        AddCategoryIfNotEmpty(result, "documents", "Документы КП", "Link_891_1802", documentRoots);

        var nomenclatureFiles = await ReadNomenclatureDocumentFilesAsync(
            sourceCode,
            src,
            fileSchema,
            policyByTable,
            projectCardObjectId,
            effectiveFileDepth,
            effectiveMaxItems,
            ct);
        AddCategoryIfNotEmpty(result, "nomenclature_documents", "Файлы изделия", "Nomenclature/Documents/DocumentFiles/Files", nomenclatureFiles);

        return result;
    }

    private async Task<IReadOnlyList<FileTreeNodeDto>> ReadTechnicalTaskRootsAsync(
        string sourceCode,
        SqlConnection src,
        FileSchema fileSchema,
        IReadOnlyDictionary<string, EffectiveRelationAccessDto> policyByTable,
        long projectCardObjectId,
        int maxDepth,
        int maxItems,
        CancellationToken ct)
    {
        var fileIds = new List<long>();

        // В некоторых базах папка ТЗ связана с карточкой проекта напрямую.
        if (policyByTable.TryGetValue("Papka_TZ_kartochka_pr_1", out var directProjectToTzFolder))
        {
            fileIds.AddRange(await ReadTargetIdsFromRelationAsync(
                src,
                directProjectToTzFolder.RelationTable,
                projectCardObjectId,
                directProjectToTzFolder.DirectionMode,
                maxItems,
                ct));
        }

        // В других случаях путь идет через объект технического задания:
        // Карточка проекта -> Техническое задание -> Папка/файлы.
        if (policyByTable.TryGetValue("Tekhnicheskoe_zadanie_Kar", out var projectToTz))
        {
            var tzIds = await ReadTargetIdsFromRelationAsync(
                src,
                projectToTz.RelationTable,
                projectCardObjectId,
                projectToTz.DirectionMode,
                maxItems,
                ct);

            foreach (var tzId in tzIds.Take(maxItems))
            {
                foreach (var tzToFileRelation in TechnicalTaskToFileRelations)
                {
                    if (!policyByTable.TryGetValue(tzToFileRelation, out var tzToFile))
                        continue;

                    fileIds.AddRange(await ReadTargetIdsFromRelationAsync(
                        src,
                        tzToFile.RelationTable,
                        tzId,
                        tzToFile.DirectionMode,
                        maxItems,
                        ct));
                }
            }
        }

        return await BuildFileTreeBatchAsync(sourceCode, src, fileSchema, fileIds, maxDepth, maxItems, ct);
    }

    private async Task<IReadOnlyList<FileTreeNodeDto>> ReadProjectDocumentRootsAsync(
        string sourceCode,
        SqlConnection src,
        FileSchema fileSchema,
        IReadOnlyDictionary<string, EffectiveRelationAccessDto> policyByTable,
        long projectCardObjectId,
        int maxDepth,
        int maxItems,
        CancellationToken ct)
    {
        var documentRootNodes = new List<FileTreeNodeDto>();

        foreach (var relationTable in ProjectToDocumentRelations)
        {
            if (!policyByTable.TryGetValue(relationTable, out var projectToDocument))
                continue;

            var documentIds = await ReadTargetIdsFromRelationAsync(
                src,
                projectToDocument.RelationTable,
                projectCardObjectId,
                projectToDocument.DirectionMode,
                maxItems,
                ct);

            foreach (var documentId in documentIds.Take(maxItems))
            {
                var documentChildren = new List<FileTreeNodeDto>();
                foreach (var documentToFileTable in DocumentToFileRelations)
                {
                    if (!policyByTable.TryGetValue(documentToFileTable, out var documentToFile))
                        continue;

                    var fileIds = await ReadTargetIdsFromRelationAsync(
                        src,
                        documentToFile.RelationTable,
                        documentId,
                        documentToFile.DirectionMode,
                        maxItems,
                        ct);

                    var fileNodes = await BuildFileTreeBatchAsync(
                        sourceCode,
                        src,
                        fileSchema,
                        fileIds,
                        maxDepth,
                        maxItems,
                        ct);

                    documentChildren.AddRange(fileNodes);
                }

                if (documentChildren.Count == 0)
                    continue;

                var relationMeta = await GetRelationMetaAsync(sourceCode, relationTable, ct);
                var documentTable = relationMeta is null
                    ? null
                    : await GetGroupTableAsync(sourceCode, relationMeta.TargetGroupIdFor(ProjectCardGroupId), ct);

                var documentPreview = string.IsNullOrWhiteSpace(documentTable)
                    ? null
                    : await ReadObjectPreviewAsync(sourceCode, src, relationMeta!.TargetGroupIdFor(ProjectCardGroupId), documentTable!, documentId, ct);

                var documentName = documentPreview?.Name ?? documentPreview?.ObjectCode ?? $"Документ {documentId.ToString(CultureInfo.InvariantCulture)}";
                documentRootNodes.Add(new FileTreeNodeDto(
                    sourceCode,
                    documentId,
                    null,
                    documentPreview?.Guid,
                    null,
                    documentName,
                    null,
                    "document",
                    null,
                    documentPreview?.DocsUrl,
                    DeduplicateNodes(documentChildren)));
            }
        }

        return DeduplicateNodes(documentRootNodes);
    }

    private async Task<IReadOnlyList<FileTreeNodeDto>> ReadNomenclatureDocumentFilesAsync(
        string sourceCode,
        SqlConnection src,
        FileSchema fileSchema,
        IReadOnlyDictionary<string, EffectiveRelationAccessDto> policyByTable,
        long projectCardObjectId,
        int maxDepth,
        int maxItems,
        CancellationToken ct)
    {
        var nomenclatureIds = new List<long>();
        foreach (var relationTable in ProjectToNomenclatureRelations)
        {
            if (!policyByTable.TryGetValue(relationTable, out var policy))
                continue;

            var ids = await ReadTargetIdsFromRelationAsync(
                src,
                policy.RelationTable,
                projectCardObjectId,
                policy.DirectionMode,
                maxItems,
                ct);
            nomenclatureIds.AddRange(ids);
        }

        if (nomenclatureIds.Count == 0)
            return Array.Empty<FileTreeNodeDto>();

        if (!await SqlHelpers.TableExistsAsync(src, "dbo", "Nomenclature", ct) ||
            !await SqlHelpers.TableExistsAsync(src, "dbo", "Documents", ct) ||
            !await SqlHelpers.TableExistsAsync(src, "dbo", "DocumentFiles", ct))
        {
            return Array.Empty<FileTreeNodeDto>();
        }

        var documentColumns = await SqlHelpers.GetColumnsAsync(src, "dbo", "Documents", ct);
        if (!documentColumns.Contains("s_NomenclatureObjectID"))
            return Array.Empty<FileTreeNodeDto>();

        var docFileColumns = await SqlHelpers.GetColumnsAsync(src, "dbo", "DocumentFiles", ct);
        var dfDeletedFilter = docFileColumns.Contains("DeletedChangelistID") ? "AND df.DeletedChangelistID = 0" : string.Empty;
        var docActualFilter = documentColumns.Contains("s_ActualVersion") ? "AND d.s_ActualVersion = 1" : string.Empty;
        var docDeletedFilter = documentColumns.Contains("s_Deleted") ? "AND d.s_Deleted = 0" : string.Empty;
        var fileActualFilter = fileSchema.HasActualVersion ? "AND f.s_ActualVersion = 1" : string.Empty;
        var fileDeletedFilter = fileSchema.HasDeleted ? "AND f.s_Deleted = 0" : string.Empty;

        var allFileIds = new List<long>();
        foreach (var nomenclatureId in nomenclatureIds.Distinct().Take(maxItems))
        {
            await using var cmd = src.CreateCommand();
            cmd.CommandTimeout = 30;
            cmd.CommandText = $@"
SELECT TOP (@maxItems)
    f.s_ObjectID
FROM dbo.Nomenclature n
INNER JOIN dbo.Documents d ON d.s_NomenclatureObjectID = n.s_ObjectID
INNER JOIN dbo.DocumentFiles df ON df.MasterID = d.s_ObjectID
INNER JOIN dbo.Files f ON f.s_ObjectID = df.SlaveID
WHERE n.s_ObjectID = @nomenclatureId
  {docActualFilter}
  {docDeletedFilter}
  {dfDeletedFilter}
  {fileActualFilter}
  {fileDeletedFilter}
ORDER BY f.s_ObjectID;";
            cmd.Parameters.Add(new SqlParameter("@nomenclatureId", SqlDbType.BigInt) { Value = nomenclatureId });
            cmd.Parameters.Add(new SqlParameter("@maxItems", SqlDbType.Int) { Value = maxItems });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                allFileIds.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        }

        return await BuildFileTreeBatchAsync(sourceCode, src, fileSchema, allFileIds, maxDepth, maxItems, ct);
    }

    private async Task<IReadOnlyList<long>> ReadTargetIdsFromRelationAsync(
        SqlConnection src,
        string relationTable,
        long sourceObjectId,
        string directionMode,
        int maxItems,
        CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(src, "dbo", relationTable, ct))
            return Array.Empty<long>();

        var columns = await SqlHelpers.GetColumnsAsync(src, "dbo", relationTable, ct);
        if (!columns.Contains("MasterID") || !columns.Contains("SlaveID"))
            return Array.Empty<long>();

        var whereClause = directionMode.Equals("MasterToSlave", StringComparison.OrdinalIgnoreCase)
            ? "MasterID = @sourceObjectId"
            : directionMode.Equals("SlaveToMaster", StringComparison.OrdinalIgnoreCase)
                ? "SlaveID = @sourceObjectId"
                : "MasterID = @sourceObjectId OR SlaveID = @sourceObjectId";

        await using var cmd = src.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT TOP (@maxItems)
    MasterID,
    SlaveID
FROM dbo.{SqlHelpers.QuoteName(relationTable)}
WHERE {whereClause};";
        cmd.Parameters.Add(new SqlParameter("@sourceObjectId", SqlDbType.BigInt) { Value = sourceObjectId });
        cmd.Parameters.Add(new SqlParameter("@maxItems", SqlDbType.Int) { Value = maxItems });

        var result = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var masterId = Convert.ToInt64(r["MasterID"], CultureInfo.InvariantCulture);
            var slaveId = Convert.ToInt64(r["SlaveID"], CultureInfo.InvariantCulture);

            if (directionMode.Equals("MasterToSlave", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(slaveId);
            }
            else if (directionMode.Equals("SlaveToMaster", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(masterId);
            }
            else
            {
                result.Add(masterId == sourceObjectId ? slaveId : masterId);
            }
        }

        return result.Distinct().Take(maxItems).ToArray();
    }

    private async Task<IReadOnlyList<FileTreeNodeDto>> BuildFileTreeBatchAsync(
        string sourceCode,
        SqlConnection src,
        FileSchema fileSchema,
        IEnumerable<long> rootIds,
        int maxDepth,
        int maxItems,
        CancellationToken ct)
    {
        var nodes = new List<FileTreeNodeDto>();
        var visited = new HashSet<long>();

        foreach (var rootId in rootIds.Distinct().Take(maxItems))
        {
            var node = await ReadFileNodeAsync(sourceCode, src, fileSchema, rootId, 0, maxDepth, maxItems, visited, ct);
            if (node is not null)
                nodes.Add(node);
        }

        return DeduplicateNodes(nodes);
    }

    private async Task<FileTreeNodeDto?> ReadFileNodeAsync(
        string sourceCode,
        SqlConnection src,
        FileSchema schema,
        long objectId,
        int currentDepth,
        int maxDepth,
        int maxItems,
        HashSet<long> visited,
        CancellationToken ct)
    {
        if (!visited.Add(objectId))
            return null;

        var selectVersion = schema.VersionColumn is null ? "CAST(NULL AS int)" : $"TRY_CONVERT(int, {SqlHelpers.QuoteName(schema.VersionColumn)})";
        var selectGuid = schema.GuidColumn is null ? "CAST(NULL AS nvarchar(36))" : $"TRY_CONVERT(nvarchar(36), {SqlHelpers.QuoteName(schema.GuidColumn)})";
        var selectParent = schema.ParentColumn is null ? "CAST(NULL AS bigint)" : $"TRY_CONVERT(bigint, {SqlHelpers.QuoteName(schema.ParentColumn)})";
        var selectName = schema.NameColumn is null ? "CAST(NULL AS nvarchar(4000))" : $"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(schema.NameColumn)})";
        var selectStage = schema.StageColumn is null ? "CAST(NULL AS nvarchar(4000))" : $"TRY_CONVERT(nvarchar(4000), {SqlHelpers.QuoteName(schema.StageColumn)})";
        var actualFilter = schema.HasActualVersion ? "AND s_ActualVersion = 1" : string.Empty;
        var deletedFilter = schema.HasDeleted ? "AND s_Deleted = 0" : string.Empty;

        await using var cmd = src.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT TOP (1)
    s_ObjectID AS ObjectId,
    {selectVersion} AS Version,
    {selectGuid} AS Guid,
    {selectParent} AS ParentId,
    {selectName} AS [Name],
    {selectStage} AS Stage
FROM dbo.Files
WHERE s_ObjectID = @objectId
  {actualFilter}
  {deletedFilter};";
        cmd.Parameters.Add(new SqlParameter("@objectId", SqlDbType.BigInt) { Value = objectId });

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        var version = r.IsDBNull(r.GetOrdinal("Version")) ? (int?)null : Convert.ToInt32(r["Version"], CultureInfo.InvariantCulture);
        var guid = r.GetNullableString("Guid");
        var parentId = r.IsDBNull(r.GetOrdinal("ParentId")) ? (long?)null : Convert.ToInt64(r["ParentId"], CultureInfo.InvariantCulture);
        var name = r.GetNullableString("Name");
        var stage = r.GetNullableString("Stage");

        await r.DisposeAsync();

        var children = new List<FileTreeNodeDto>();
        if (schema.ParentColumn is not null && currentDepth < maxDepth)
        {
            var childIds = await ReadChildFileIdsAsync(src, schema, objectId, maxItems, ct);
            foreach (var childId in childIds)
            {
                var child = await ReadFileNodeAsync(sourceCode, src, schema, childId, currentDepth + 1, maxDepth, maxItems, visited, ct);
                if (child is not null)
                    children.Add(child);
            }
        }

        var extension = GetExtensionOrNull(name);
        var nodeKind = children.Count > 0
            ? "folder"
            : extension is null
                ? "file_or_folder"
                : "file";

        var downloadUrl = nodeKind == "file"
            ? BuildFileDownloadUrl(sourceCode, objectId, version)
            : null;

        return new FileTreeNodeDto(
            sourceCode,
            objectId,
            version,
            guid,
            parentId,
            name,
            extension,
            nodeKind,
            stage,
            downloadUrl,
            DeduplicateNodes(children));
    }

    private async Task<IReadOnlyList<long>> ReadChildFileIdsAsync(SqlConnection src, FileSchema schema, long parentId, int maxItems, CancellationToken ct)
    {
        if (schema.ParentColumn is null)
            return Array.Empty<long>();

        var actualFilter = schema.HasActualVersion ? "AND s_ActualVersion = 1" : string.Empty;
        var deletedFilter = schema.HasDeleted ? "AND s_Deleted = 0" : string.Empty;
        var orderBy = schema.NameColumn is null ? "s_ObjectID" : SqlHelpers.QuoteName(schema.NameColumn);

        await using var cmd = src.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT TOP (@maxItems)
    s_ObjectID
FROM dbo.Files
WHERE {SqlHelpers.QuoteName(schema.ParentColumn)} = @parentId
  {actualFilter}
  {deletedFilter}
ORDER BY {orderBy}, s_ObjectID;";
        cmd.Parameters.Add(new SqlParameter("@parentId", SqlDbType.BigInt) { Value = parentId });
        cmd.Parameters.Add(new SqlParameter("@maxItems", SqlDbType.Int) { Value = maxItems });

        var result = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        return result;
    }

    private async Task<ObjectPreviewDto?> ReadObjectPreviewAsync(string sourceCode, SqlConnection src, int? groupId, string tableName, long objectId, CancellationToken ct)
    {
        if (groupId is null || !await SqlHelpers.TableExistsAsync(src, "dbo", tableName, ct))
            return null;

        var cols = await SqlHelpers.GetColumnsAsync(src, "dbo", tableName, ct);
        if (!cols.Contains("s_ObjectID"))
            return null;

        var guidCol = cols.Contains("s_Guid") ? "s_Guid" : null;
        var codeCol = SqlHelpers.FirstExisting(cols, "Obekt", "Obect", "Object", "Shifr_izdeliya", "Code", "Designation", "Number", "Denotation");
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
            return null;

        return new ObjectPreviewDto(
            groupId,
            tableName,
            objectId,
            r.GetNullableString("Guid"),
            r.GetNullableString("ObjectCode"),
            r.GetNullableString("Name"),
            await BuildDocsUrlAsync(sourceCode, groupId.Value, objectId, ct));
    }

    private async Task<string?> BuildDocsUrlAsync(string sourceCode, int groupId, long objectId, CancellationToken ct)
    {
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = "SELECT BaseDocsUrl FROM app.Source WHERE Code = @code";
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 100) { Value = sourceCode });
        var baseUrl = Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return baseUrl.TrimEnd('/') + $"/OpenPropertiesInNewWindow/?refId={groupId}&objId={objectId}";
    }

    private async Task<string?> GetGroupTableAsync(string sourceCode, int? groupId, CancellationToken ct)
    {
        if (groupId is null)
            return null;

        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
SELECT g.TableName
FROM app.TflexGroup g
JOIN app.Source s ON s.SourceId = g.SourceId
WHERE s.Code = @sourceCode AND g.GroupId = @groupId;";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
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

    private static void AddCategoryIfNotEmpty(List<FileCategoryDto> result, string code, string title, string? relationTable, IReadOnlyList<FileTreeNodeDto> roots)
    {
        if (roots.Count == 0)
            return;

        var flat = FlattenFiles(roots).ToArray();
        result.Add(new FileCategoryDto(code, title, relationTable, roots.Count, flat.Length, roots, flat));
    }

    private static IEnumerable<FileTreeNodeDto> FlattenFiles(IEnumerable<FileTreeNodeDto> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.NodeKind == "file" || node.Children.Count == 0)
                yield return node;

            foreach (var child in FlattenFiles(node.Children))
                yield return child;
        }
    }

    private static IReadOnlyList<FileTreeNodeDto> DeduplicateNodes(IEnumerable<FileTreeNodeDto> nodes)
    {
        return nodes
            .GroupBy(x => x.ObjectId)
            .Select(g => g.First())
            .OrderBy(x => x.NodeKind == "folder" ? 0 : 1)
            .ThenBy(x => x.Name)
            .ThenBy(x => x.ObjectId)
            .ToArray();
    }

    private static string? GetExtensionOrNull(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var ext = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(ext) ? null : ext.TrimStart('.').ToLowerInvariant();
    }

    private static string? BuildFileDownloadUrl(string sourceCode, long objectId, int? version)
    {
        if (version is null)
            return null;

        return "/api/files/objectid" +
               $"?srvName={Uri.EscapeDataString(sourceCode)}" +
               $"&folder={objectId.ToString(CultureInfo.InvariantCulture)}" +
               $"&fileName={version.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private sealed record DirectFileCategory(string Code, string Title, string RelationTable);

    private sealed record RelationMeta(string? RelationCaption, int? MasterGroupId, int? SlaveGroupId)
    {
        public int? TargetGroupIdFor(int sourceGroupId)
        {
            if (MasterGroupId == sourceGroupId) return SlaveGroupId;
            if (SlaveGroupId == sourceGroupId) return MasterGroupId;
            return SlaveGroupId ?? MasterGroupId;
        }
    }

    private sealed record FileSchema(
        bool HasObjectId,
        bool HasActualVersion,
        bool HasDeleted,
        string? VersionColumn,
        string? GuidColumn,
        string? ParentColumn,
        string? NameColumn,
        string? StageColumn)
    {
        public static async Task<FileSchema> ReadAsync(SqlConnection src, CancellationToken ct)
        {
            var columns = await SqlHelpers.GetColumnsAsync(src, "dbo", "Files", ct);
            return new FileSchema(
                columns.Contains("s_ObjectID"),
                columns.Contains("s_ActualVersion"),
                columns.Contains("s_Deleted"),
                SqlHelpers.FirstExisting(columns, "s_Version", "Version"),
                columns.Contains("s_Guid") ? "s_Guid" : null,
                SqlHelpers.FirstExisting(columns, "s_ParentID", "ParentID", "ParentId"),
                SqlHelpers.FirstExisting(columns, "Name", "OriginalName", "FileName", "DisplayName"),
                SqlHelpers.FirstExisting(columns, "Stage", "Stadia", "Status", "s_State"));
        }
    }
}
