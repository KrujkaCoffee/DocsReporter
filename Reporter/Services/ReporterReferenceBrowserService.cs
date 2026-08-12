using System.Data;
using System.Globalization;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface IReporterReferenceBrowserService
{
    Task<IReadOnlyList<ReporterReferenceCatalogItemDto>> GetCatalogAsync(
        ClaimsPrincipal user,
        IReadOnlyCollection<string>? sourceCodes,
        CancellationToken ct);

    Task<ReporterReferenceSchemaDto> GetSchemaAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        CancellationToken ct);

    Task<ReporterReferenceItemsPageDto> GetItemsAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        string? query,
        long? parentId,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<ReporterReferenceObjectDto?> GetObjectAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        long objectId,
        CancellationToken ct);
}

public sealed class ReporterReferenceBrowserService : IReporterReferenceBrowserService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] CodeCandidates =
    {
        "Obekt", "Obect", "Object", "Shifr_izdeliya", "Designation",
        "Code", "Number", "Nomer", "ShortName"
    };

    private static readonly string[] NameCandidates =
    {
        "Name", "Naimenovanie", "Nazvanie", "FullName", "OriginalName",
        "FileName", "Caption", "Title"
    };

    private static readonly HashSet<string> AllowedSystemFields = new(
        new[] { "s_ObjectID", "s_Guid", "s_ParentID", "s_CreationDate", "s_EditDate", "s_State" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BlockedTypes = new(
        new[] { "binary", "varbinary", "image", "timestamp", "rowversion", "sql_variant", "geometry", "geography", "hierarchyid" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SensitiveNameFragments =
    {
        "password", "passwd", "secret", "token", "credential", "signatureimage",
        "photo", "icon", "binary", "blob", "sidonly", "masterpassword"
    };

    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["s_ObjectID"] = "ID объекта",
            ["s_Guid"] = "UUID",
            ["s_ParentID"] = "Родительский объект",
            ["s_CreationDate"] = "Дата создания",
            ["s_EditDate"] = "Дата изменения",
            ["s_State"] = "Состояние",
            ["Obekt"] = "Обозначение",
            ["Obect"] = "Обозначение",
            ["Object"] = "Обозначение",
            ["Designation"] = "Обозначение",
            ["Shifr_izdeliya"] = "Шифр изделия",
            ["Name"] = "Наименование",
            ["Naimenovanie"] = "Наименование",
            ["Nazvanie"] = "Наименование",
            ["FullName"] = "Полное наименование",
            ["Nomer_proekta"] = "Номер проекта",
            ["Nomer_pozitsii"] = "Номер позиции",
            ["Kommentariy"] = "Комментарий",
            ["Description"] = "Описание",
            ["Comment"] = "Комментарий",
            ["Stage"] = "Этап",
            ["Status"] = "Статус",
            ["FileName"] = "Имя файла",
            ["OriginalName"] = "Исходное имя"
        };

    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _access;
    private readonly ITflexAccessDecisionService _accessDecision;
    private readonly ReporterOptions _options;

    public ReporterReferenceBrowserService(
        IReporterSqlConnectionFactory factory,
        IReporterAccessService access,
        ITflexAccessDecisionService accessDecision,
        IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _access = access;
        _accessDecision = accessDecision;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<ReporterReferenceCatalogItemDto>> GetCatalogAsync(
        ClaimsPrincipal user,
        IReadOnlyCollection<string>? sourceCodes,
        CancellationToken ct)
    {
        var appUserId = await _access.EnsureAppUserAsync(user, ct);
        var requested = sourceCodes is { Count: > 0 }
            ? sourceCodes.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var rows = new List<CatalogRow>();
        await using var cn = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = @"
SELECT
    s.Code AS SourceCode,
    s.DisplayName AS SourceDisplayName,
    g.GroupId,
    TRY_CONVERT(nvarchar(36), g.GroupGuid) AS GroupGuid,
    g.TableName,
    COALESCE(NULLIF(g.Caption, N''), g.TableName) AS Caption,
    g.HierarchyType,
    CAST(MAX(CAST(p.CanSearch AS int)) AS bit) AS CanSearch,
    CAST(MAX(CAST(p.CanOpenCard AS int)) AS bit) AS CanOpenCard,
    MAX(p.MaxRowsPerPage) AS MaxRowsPerPage
FROM app.TflexGroup g
JOIN app.Source s
  ON s.SourceId = g.SourceId
 AND s.IsEnabled = 1
JOIN app.GroupAccessPolicy p
  ON p.GroupId = g.GroupId
 AND (p.SourceId IS NULL OR p.SourceId = s.SourceId)
JOIN app.AppUserRole ur
  ON ur.AppRoleId = p.AppRoleId
 AND ur.AppUserId = @appUserId
WHERE p.IsEnabled = 1
  AND p.CanSeeInMenu = 1
  AND g.Type <> 5
  AND g.TableName IS NOT NULL
GROUP BY
    s.Code,
    s.DisplayName,
    g.GroupId,
    g.GroupGuid,
    g.TableName,
    g.Caption,
    g.HierarchyType
ORDER BY
    COALESCE(NULLIF(g.Caption, N''), g.TableName),
    s.DisplayName,
    s.Code;";
        cmd.Parameters.Add(new SqlParameter("@appUserId", SqlDbType.Int) { Value = appUserId });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sourceCode = reader.GetString(reader.GetOrdinal("SourceCode"));
            if (requested is not null && !requested.Contains(sourceCode))
                continue;

            rows.Add(new CatalogRow(
                sourceCode,
                reader.GetString(reader.GetOrdinal("SourceDisplayName")),
                reader.GetInt32Flexible("GroupId"),
                reader.GetNullableString("GroupGuid"),
                reader.GetString(reader.GetOrdinal("TableName")),
                reader.GetString(reader.GetOrdinal("Caption")),
                ReadNullableInt(reader, "HierarchyType"),
                ReadBool(reader, "CanSearch"),
                ReadBool(reader, "CanOpenCard"),
                Math.Max(1, reader.GetInt32Flexible("MaxRowsPerPage"))));
        }

        return rows
            .GroupBy(row => row.TableName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var bindings = group
                    .OrderBy(row => row.SourceDisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(row => row.SourceCode, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new ReporterReferenceCatalogSourceDto(
                        row.SourceCode,
                        row.SourceDisplayName,
                        row.GroupId,
                        row.GroupGuid,
                        row.HierarchyType,
                        row.CanSearch,
                        row.CanOpenCard,
                        row.MaxRowsPerPage))
                    .Take(Math.Clamp(_options.ReferenceBrowserMaxSources, 1, 50))
                    .ToArray();

                var distinctGuids = bindings
                    .Select(binding => binding.GroupGuid)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var caption = group
                    .Select(row => row.Caption)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? group.Key;

                return new ReporterReferenceCatalogItemDto(
                    $"table:{group.Key.ToUpperInvariant()}",
                    group.Key,
                    caption,
                    distinctGuids.Length == 1 ? distinctGuids[0] : null,
                    group.Any(row => (row.HierarchyType ?? 0) > 0),
                    bindings);
            })
            .OrderBy(item => item.Caption, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.TableName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ReporterReferenceSchemaDto> GetSchemaAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        CancellationToken ct)
    {
        var context = await GetContextAsync(user, sourceCode, groupId, ct);
        await using var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        var fields = await ReadFieldsAsync(src, context.TableName, ct);
        return BuildSchema(context, fields);
    }

    public async Task<ReporterReferenceItemsPageDto> GetItemsAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        string? query,
        long? parentId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var context = await GetContextAsync(user, sourceCode, groupId, ct);
        await using var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        var physicalFields = await ReadFieldsAsync(src, context.TableName, ct);
        var schema = BuildSchema(context, physicalFields);

        page = Math.Max(1, page);
        var configuredMax = Math.Clamp(_options.ReferenceBrowserMaxPageSize, 1, 500);
        var policyMax = Math.Clamp(context.Access?.MaxRowsPerPage ?? configuredMax, 1, 500);
        pageSize = Math.Clamp(pageSize <= 0 ? _options.ReferenceBrowserDefaultPageSize : pageSize, 1, Math.Min(configuredMax, policyMax));
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        var rows = await ReadItemsAsync(
            src,
            context,
            schema,
            physicalFields,
            query,
            parentId,
            page,
            pageSize,
            ct);

        var warnings = new List<string>();
        var hasMore = rows.Count > pageSize;
        var candidates = rows.Take(pageSize).ToArray();

        if (_accessDecision.ShouldEnforceTflexRead)
        {
            var filtered = new List<ReporterReferenceObjectRowDto>(candidates.Length);
            foreach (var item in candidates)
            {
                var decision = await _accessDecision.CanReadAsync(
                    user,
                    sourceCode,
                    groupId,
                    item.ObjectId,
                    ct);

                if (decision.Allowed)
                    filtered.Add(item);
            }

            candidates = filtered.ToArray();
            warnings.Add("В строгом режиме права проверяются для каждой строки текущей страницы; после фильтрации страница может содержать меньше выбранного количества строк.");
        }

        return new ReporterReferenceItemsPageDto(
            sourceCode,
            groupId,
            context.TableName,
            context.Caption,
            schema.HasHierarchy,
            query,
            parentId,
            page,
            pageSize,
            hasMore,
            query is not null,
            candidates,
            warnings);
    }

    public async Task<ReporterReferenceObjectDto?> GetObjectAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        long objectId,
        CancellationToken ct)
    {
        var context = await GetContextAsync(user, sourceCode, groupId, ct);

        if (_accessDecision.ShouldEnforceTflexRead)
        {
            var decision = await _accessDecision.CanReadAsync(
                user,
                sourceCode,
                groupId,
                objectId,
                ct);

            if (!decision.Allowed)
                throw new UnauthorizedAccessException(decision.Reason);
        }

        await using var src = await _factory.OpenSourceConnectionAsync(sourceCode, ct);
        var physicalFields = await ReadFieldsAsync(src, context.TableName, ct);
        var schema = BuildSchema(context, physicalFields);
        var row = await ReadObjectAsync(src, context, schema, physicalFields, objectId, ct);

        return row is null
            ? null
            : new ReporterReferenceObjectDto(
                sourceCode,
                groupId,
                context.TableName,
                context.Caption,
                row,
                schema.Fields);
    }

    private async Task<GroupContext> GetContextAsync(
        ClaimsPrincipal user,
        string sourceCode,
        int groupId,
        CancellationToken ct)
    {
        EffectiveGroupAccessDto? access = null;
        if (_accessDecision.ShouldEnforceAppPolicy)
        {
            access = await _access.GetGroupAccessAsync(user, sourceCode, groupId, ct);
            if (access is null || (!access.CanSeeInMenu && !access.CanSearch && !access.CanOpenCard))
                throw new UnauthorizedAccessException("Нет доступа к выбранному справочнику в политике репортера.");
        }

        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = app.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (1)
    s.DisplayName AS SourceDisplayName,
    s.BaseDocsUrl,
    g.GroupId,
    TRY_CONVERT(nvarchar(36), g.GroupGuid) AS GroupGuid,
    g.TableName,
    COALESCE(NULLIF(g.Caption, N''), g.TableName) AS Caption,
    g.Type,
    g.HierarchyType
FROM app.TflexGroup g
JOIN app.Source s
  ON s.SourceId = g.SourceId
WHERE s.Code = @sourceCode
  AND s.IsEnabled = 1
  AND g.GroupId = @groupId
  AND g.Type <> 5;";
        cmd.Parameters.Add(new SqlParameter("@sourceCode", SqlDbType.NVarChar, 100) { Value = sourceCode });
        cmd.Parameters.Add(new SqlParameter("@groupId", SqlDbType.Int) { Value = groupId });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Справочник {groupId} не найден для источника '{sourceCode}'. Сначала выполните синхронизацию метаданных.");

        return new GroupContext(
            sourceCode,
            reader.GetString(reader.GetOrdinal("SourceDisplayName")),
            reader.GetNullableString("BaseDocsUrl"),
            reader.GetInt32Flexible("GroupId"),
            reader.GetNullableString("GroupGuid"),
            reader.GetString(reader.GetOrdinal("TableName")),
            reader.GetString(reader.GetOrdinal("Caption")),
            reader.GetInt32Flexible("Type"),
            ReadNullableInt(reader, "HierarchyType"),
            access);
    }

    private async Task<IReadOnlyList<PhysicalField>> ReadFieldsAsync(
        SqlConnection src,
        string tableName,
        CancellationToken ct)
    {
        if (!await SqlHelpers.TableExistsAsync(src, "dbo", tableName, ct))
            throw new InvalidOperationException($"Физическая таблица dbo.{tableName} не найдена.");

        await using var cmd = src.CreateCommand();
        cmd.CommandText = @"
SELECT
    c.name AS ColumnName,
    t.name AS DataType,
    c.max_length AS MaxLength,
    c.column_id AS ColumnId,
    c.is_nullable AS IsNullable
FROM sys.columns c
JOIN sys.types t
  ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.' + @tableName)
ORDER BY c.column_id;";
        cmd.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = tableName });

        var result = new List<PhysicalField>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PhysicalField(
                reader.GetString(reader.GetOrdinal("ColumnName")),
                reader.GetString(reader.GetOrdinal("DataType")),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("MaxLength")), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("ColumnId")), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(reader.GetOrdinal("IsNullable")), CultureInfo.InvariantCulture)));
        }

        if (!result.Any(field => field.Name.Equals("s_ObjectID", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Таблица dbo.{tableName} не содержит s_ObjectID и не может быть открыта универсальным просмотрщиком.");

        return result;
    }

    private ReporterReferenceSchemaDto BuildSchema(
        GroupContext context,
        IReadOnlyList<PhysicalField> physicalFields)
    {
        var byName = physicalFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var codeField = FirstExisting(byName, CodeCandidates);
        var nameField = FirstExisting(byName, NameCandidates);
        var maxFields = Math.Clamp(_options.ReferenceBrowserMaxFields, 4, 64);

        var candidates = physicalFields
            .Where(IsDisplayable)
            .Select(field => new
            {
                Field = field,
                Priority = GetPriority(field.Name, codeField, nameField)
            })
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Field.ColumnId)
            .Take(maxFields)
            .ToArray();

        var searchableNames = candidates
            .Where(item => IsStringType(item.Field.DataType) && !item.Field.Name.StartsWith("s_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Priority)
            .Take(6)
            .Select(item => item.Field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (codeField is not null)
            searchableNames.Add(codeField);
        if (nameField is not null)
            searchableNames.Add(nameField);

        var fields = candidates
            .Select(item => new ReporterReferenceFieldDto(
                item.Field.Name,
                GetLabel(item.Field.Name),
                item.Field.DataType,
                item.Field.IsNullable,
                item.Field.Name.StartsWith("s_", StringComparison.OrdinalIgnoreCase),
                item.Field.Name.Equals(codeField, StringComparison.OrdinalIgnoreCase)
                    || item.Field.Name.Equals(nameField, StringComparison.OrdinalIgnoreCase),
                searchableNames.Contains(item.Field.Name),
                item.Priority * 100 + item.Field.ColumnId))
            .ToArray();

        return new ReporterReferenceSchemaDto(
            context.SourceCode,
            context.SourceDisplayName,
            context.GroupId,
            context.GroupGuid,
            context.TableName,
            context.Caption,
            byName.ContainsKey("s_ParentID"),
            codeField,
            nameField,
            Math.Clamp(context.Access?.MaxRowsPerPage ?? _options.ReferenceBrowserMaxPageSize, 1, 500),
            fields);
    }

    private async Task<IReadOnlyList<ReporterReferenceObjectRowDto>> ReadItemsAsync(
        SqlConnection src,
        GroupContext context,
        ReporterReferenceSchemaDto schema,
        IReadOnlyList<PhysicalField> physicalFields,
        string? query,
        long? parentId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var byName = physicalFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var valueFields = schema.Fields
            .Where(field => !field.Name.Equals("s_ObjectID", StringComparison.OrdinalIgnoreCase)
                         && !field.Name.Equals("s_Guid", StringComparison.OrdinalIgnoreCase)
                         && !field.Name.Equals("s_ParentID", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var valueSelect = valueFields.Length == 0
            ? string.Empty
            : ",\n    " + string.Join(",\n    ", valueFields.Select(field =>
                $"{ToTextExpression("src", field.Name, field.DataType)} AS {SqlHelpers.QuoteName(field.Name)}"));

        var guidSelect = byName.ContainsKey("s_Guid")
            ? "TRY_CONVERT(nvarchar(36), src.s_Guid)"
            : "CAST(NULL AS nvarchar(36))";
        var parentSelect = schema.HasHierarchy
            ? "TRY_CONVERT(bigint, src.s_ParentID)"
            : "CAST(NULL AS bigint)";

        var actualFilter = byName.ContainsKey("s_ActualVersion") ? "AND src.s_ActualVersion = 1" : string.Empty;
        var deletedFilter = byName.ContainsKey("s_Deleted") ? "AND src.s_Deleted = 0" : string.Empty;
        var childActualFilter = byName.ContainsKey("s_ActualVersion") ? "AND child.s_ActualVersion = 1" : string.Empty;
        var childDeletedFilter = byName.ContainsKey("s_Deleted") ? "AND child.s_Deleted = 0" : string.Empty;

        var hierarchyFilter = string.Empty;
        if (query is null && schema.HasHierarchy)
        {
            hierarchyFilter = parentId.HasValue
                ? "AND TRY_CONVERT(bigint, src.s_ParentID) = @parentId"
                : "AND ISNULL(TRY_CONVERT(bigint, src.s_ParentID), 0) = 0";
        }

        var searchFields = schema.Fields
            .Where(field => field.IsSearchable && byName.ContainsKey(field.Name))
            .Take(6)
            .ToArray();
        var searchPredicate = searchFields.Length == 0
            ? "1 = 0"
            : string.Join(" OR ", searchFields.Select(field =>
                $"TRY_CONVERT(nvarchar(4000), src.{SqlHelpers.QuoteName(field.Name)}) LIKE N'%' + @query + N'%'"));

        var hasChildren = schema.HasHierarchy
            ? $@"CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.{SqlHelpers.QuoteName(context.TableName)} child
        WHERE TRY_CONVERT(bigint, child.s_ParentID) = TRY_CONVERT(bigint, src.s_ObjectID)
          {childActualFilter}
          {childDeletedFilter}
    ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END"
            : "CAST(0 AS bit)";

        var orderParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(schema.CodeField) && byName.ContainsKey(schema.CodeField))
            orderParts.Add($"TRY_CONVERT(nvarchar(4000), src.{SqlHelpers.QuoteName(schema.CodeField)})");
        if (!string.IsNullOrWhiteSpace(schema.NameField) && byName.ContainsKey(schema.NameField))
            orderParts.Add($"TRY_CONVERT(nvarchar(4000), src.{SqlHelpers.QuoteName(schema.NameField)})");
        orderParts.Add("TRY_CONVERT(bigint, src.s_ObjectID)");

        await using var cmd = src.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT
    TRY_CONVERT(bigint, src.s_ObjectID) AS __ObjectId,
    {guidSelect} AS __Guid,
    {parentSelect} AS __ParentId,
    {hasChildren} AS __HasChildren
    {valueSelect}
FROM dbo.{SqlHelpers.QuoteName(context.TableName)} src
WHERE 1 = 1
  {actualFilter}
  {deletedFilter}
  {hierarchyFilter}
  AND (@query IS NULL OR ({searchPredicate}))
ORDER BY {string.Join(", ", orderParts)}
OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";

        cmd.Parameters.Add(new SqlParameter("@query", SqlDbType.NVarChar, 4000) { Value = (object?)query ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@parentId", SqlDbType.BigInt) { Value = (object?)parentId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = (page - 1) * pageSize });
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = pageSize + 1 });

        var result = new List<ReporterReferenceObjectRowDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadRow(reader, context, schema, valueFields));

        return result;
    }

    private async Task<ReporterReferenceObjectRowDto?> ReadObjectAsync(
        SqlConnection src,
        GroupContext context,
        ReporterReferenceSchemaDto schema,
        IReadOnlyList<PhysicalField> physicalFields,
        long objectId,
        CancellationToken ct)
    {
        var byName = physicalFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var valueFields = schema.Fields
            .Where(field => !field.Name.Equals("s_ObjectID", StringComparison.OrdinalIgnoreCase)
                         && !field.Name.Equals("s_Guid", StringComparison.OrdinalIgnoreCase)
                         && !field.Name.Equals("s_ParentID", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var valueSelect = valueFields.Length == 0
            ? string.Empty
            : ",\n    " + string.Join(",\n    ", valueFields.Select(field =>
                $"{ToTextExpression("src", field.Name, field.DataType)} AS {SqlHelpers.QuoteName(field.Name)}"));

        var guidSelect = byName.ContainsKey("s_Guid")
            ? "TRY_CONVERT(nvarchar(36), src.s_Guid)"
            : "CAST(NULL AS nvarchar(36))";
        var parentSelect = schema.HasHierarchy
            ? "TRY_CONVERT(bigint, src.s_ParentID)"
            : "CAST(NULL AS bigint)";
        var actualFilter = byName.ContainsKey("s_ActualVersion") ? "AND src.s_ActualVersion = 1" : string.Empty;
        var deletedFilter = byName.ContainsKey("s_Deleted") ? "AND src.s_Deleted = 0" : string.Empty;
        var childActualFilter = byName.ContainsKey("s_ActualVersion") ? "AND child.s_ActualVersion = 1" : string.Empty;
        var childDeletedFilter = byName.ContainsKey("s_Deleted") ? "AND child.s_Deleted = 0" : string.Empty;

        var hasChildren = schema.HasHierarchy
            ? $@"CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.{SqlHelpers.QuoteName(context.TableName)} child
        WHERE TRY_CONVERT(bigint, child.s_ParentID) = TRY_CONVERT(bigint, src.s_ObjectID)
          {childActualFilter}
          {childDeletedFilter}
    ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END"
            : "CAST(0 AS bit)";

        await using var cmd = src.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
SELECT TOP (1)
    TRY_CONVERT(bigint, src.s_ObjectID) AS __ObjectId,
    {guidSelect} AS __Guid,
    {parentSelect} AS __ParentId,
    {hasChildren} AS __HasChildren
    {valueSelect}
FROM dbo.{SqlHelpers.QuoteName(context.TableName)} src
WHERE TRY_CONVERT(bigint, src.s_ObjectID) = @objectId
  {actualFilter}
  {deletedFilter};";
        cmd.Parameters.Add(new SqlParameter("@objectId", SqlDbType.BigInt) { Value = objectId });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadRow(reader, context, schema, valueFields)
            : null;
    }

    private static ReporterReferenceObjectRowDto ReadRow(
        SqlDataReader reader,
        GroupContext context,
        ReporterReferenceSchemaDto schema,
        IReadOnlyList<ReporterReferenceFieldDto> valueFields)
    {
        var objectId = reader.GetInt64Flexible("__ObjectId");
        var guid = reader.GetNullableString("__Guid");
        var parentOrdinal = reader.GetOrdinal("__ParentId");
        var parentId = reader.IsDBNull(parentOrdinal)
            ? (long?)null
            : Convert.ToInt64(reader.GetValue(parentOrdinal), CultureInfo.InvariantCulture);
        var hasChildrenOrdinal = reader.GetOrdinal("__HasChildren");
        var hasChildren = !reader.IsDBNull(hasChildrenOrdinal)
            && Convert.ToBoolean(reader.GetValue(hasChildrenOrdinal), CultureInfo.InvariantCulture);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in valueFields)
            values[field.Name] = reader.GetNullableString(field.Name);

        var objectCode = schema.CodeField is not null && values.TryGetValue(schema.CodeField, out var code)
            ? code
            : null;
        var name = schema.NameField is not null && values.TryGetValue(schema.NameField, out var displayName)
            ? displayName
            : null;

        return new ReporterReferenceObjectRowDto(
            objectId,
            guid,
            parentId,
            hasChildren,
            objectCode,
            name,
            BuildDocsUrl(context.BaseDocsUrl, context.GroupId, objectId),
            values);
    }

    private static string ToTextExpression(string alias, string fieldName, string dataType)
    {
        var column = $"{alias}.{SqlHelpers.QuoteName(fieldName)}";
        if (IsDateType(dataType))
            return $"CONVERT(nvarchar(33), {column}, 126)";
        if (dataType.Equals("bit", StringComparison.OrdinalIgnoreCase))
            return $"CASE WHEN {column} = 1 THEN N'Да' WHEN {column} = 0 THEN N'Нет' ELSE NULL END";
        return $"TRY_CONVERT(nvarchar(max), {column})";
    }

    private static bool IsDisplayable(PhysicalField field)
    {
        if (BlockedTypes.Contains(field.DataType))
            return false;

        if (SensitiveNameFragments.Any(fragment => field.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (field.Name.StartsWith("s_", StringComparison.OrdinalIgnoreCase))
            return AllowedSystemFields.Contains(field.Name);

        return true;
    }

    private static int GetPriority(string name, string? codeField, string? nameField)
    {
        if (name.Equals(codeField, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals(nameField, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("s_CreationDate", StringComparison.OrdinalIgnoreCase)) return 80;
        if (name.Equals("s_EditDate", StringComparison.OrdinalIgnoreCase)) return 81;
        if (name.StartsWith("s_", StringComparison.OrdinalIgnoreCase)) return 90;
        return 10;
    }

    private static string GetLabel(string name)
    {
        if (Labels.TryGetValue(name, out var known))
            return known;

        var value = name.Replace('_', ' ').Trim();
        if (value.Length == 0)
            return name;

        return char.ToUpper(value[0], Ru) + value[1..];
    }

    private static string? FirstExisting(
        IReadOnlyDictionary<string, PhysicalField> fields,
        IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (fields.ContainsKey(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsStringType(string type) =>
        type.Equals("char", StringComparison.OrdinalIgnoreCase)
        || type.Equals("varchar", StringComparison.OrdinalIgnoreCase)
        || type.Equals("nchar", StringComparison.OrdinalIgnoreCase)
        || type.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
        || type.Equals("text", StringComparison.OrdinalIgnoreCase)
        || type.Equals("ntext", StringComparison.OrdinalIgnoreCase)
        || type.Equals("uniqueidentifier", StringComparison.OrdinalIgnoreCase);

    private static bool IsDateType(string type) =>
        type.Equals("date", StringComparison.OrdinalIgnoreCase)
        || type.Equals("datetime", StringComparison.OrdinalIgnoreCase)
        || type.Equals("datetime2", StringComparison.OrdinalIgnoreCase)
        || type.Equals("smalldatetime", StringComparison.OrdinalIgnoreCase)
        || type.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase)
        || type.Equals("time", StringComparison.OrdinalIgnoreCase);

    private static string? BuildDocsUrl(string? baseUrl, int groupId, long objectId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        return baseUrl.TrimEnd('/')
            + $"/OpenPropertiesInNewWindow/?refId={groupId}&objId={objectId}";
    }

    private static int? ReadNullableInt(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static bool ReadBool(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal)
            && Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private sealed record CatalogRow(
        string SourceCode,
        string SourceDisplayName,
        int GroupId,
        string? GroupGuid,
        string TableName,
        string Caption,
        int? HierarchyType,
        bool CanSearch,
        bool CanOpenCard,
        int MaxRowsPerPage);

    private sealed record GroupContext(
        string SourceCode,
        string SourceDisplayName,
        string? BaseDocsUrl,
        int GroupId,
        string? GroupGuid,
        string TableName,
        string Caption,
        int Type,
        int? HierarchyType,
        EffectiveGroupAccessDto? Access);

    private sealed record PhysicalField(
        string Name,
        string DataType,
        int MaxLength,
        int ColumnId,
        bool IsNullable);
}
