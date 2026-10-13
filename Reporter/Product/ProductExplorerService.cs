using System.Data;
using System.Globalization;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Product;

public sealed class ProductExplorerProblem : Exception
{
    public int Status { get; }
    public ProductExplorerProblem(int status, string message) : base(message) => Status = status;
}

public sealed class ProductExplorerService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IReporterAccessService _access;
    private readonly ITflexAccessDecisionService _decisions;
    private readonly ProductExplorerOptions _options;

    public ProductExplorerService(IReporterSqlConnectionFactory factory, IReporterAccessService access,
        ITflexAccessDecisionService decisions, IOptions<ProductExplorerOptions> options)
    {
        _factory = factory; _access = access; _decisions = decisions; _options = options.Value;
    }

    public async Task<IReadOnlyList<ProductSourceDto>> SourcesAsync(CancellationToken ct)
    {
        await using var app = await _factory.OpenAppConnectionAsync(ct);
        await using var cmd = Command(app, "SELECT Code, DisplayName FROM app.Source WHERE IsEnabled = 1 ORDER BY DisplayName, Code;");
        var result = new List<ProductSourceDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            if (_options.Sources.Keys.Any(k => k.Equals(code, StringComparison.OrdinalIgnoreCase)))
                result.Add(new(code, reader.GetString(1)));
        }
        return result;
    }

    public async Task<ProductPageDto<ProductCardDto>> SearchAsync(ClaimsPrincipal user, string source,
        string? query, long after, CancellationToken ct)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2 || query.Length > 200)
            throw new ArgumentException("Введите от 2 до 200 символов обозначения или наименования.");
        ValidateCursor(after);
        await using var ctx = await OpenAsync(user, source, ct);
        await RequireGroupAsync(user, ctx, ctx.Card.Id, true, ct);
        return await ScanAsync(after, async (cursor, take) =>
        {
            await using var cmd = Command(ctx.Cn, $@"
SELECT TOP (@take) n.s_ObjectID, n.Obekt, n.Name
FROM dbo.{ProductSql.Q(ctx.Card.Table)} n
WHERE {ProductSql.Saved("n")} AND n.s_ObjectID > @after
  AND (n.Obekt LIKE @query ESCAPE N'~' OR n.Name LIKE @query ESCAPE N'~')
ORDER BY n.s_ObjectID;");
            Add(cmd, "@query", SqlDbType.NVarChar, "%" + ProductExplorerRules.EscapeLike(query) + "%", 500);
            Add(cmd, "@after", SqlDbType.BigInt, cursor); Add(cmd, "@take", SqlDbType.Int, take);
            var list = new List<ProductCardDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(new(Long(reader, 0), Text(reader, 1), Text(reader, 2)));
            if (list.Select(x => x.ObjectId).Distinct().Count() != list.Count) throw Ambiguous();
            return list;
        }, x => x.ObjectId, x => CanReadAsync(user, ctx, ctx.Card.Id, x.ObjectId, ct), ct);
    }

    public async Task<ProductCardRootsDto> RootsAsync(ClaimsPrincipal user, string source, long cardId,
        string? date, CancellationToken ct)
    {
        var at = ProductExplorerRules.ParseDate(date);
        await using var ctx = await OpenAsync(user, source, ct);
        var card = await ReadCardAsync(user, ctx, cardId, ct);
        var ids = await TargetsAsync(user, ctx, ctx.CardProduct, ctx.Card.Id, ctx.Product.Id, cardId, ct);
        var roots = new List<ProductObjectDto>();
        foreach (var id in ids)
        {
            var obj = await ReadProductAsync(ctx, id, ct);
            if (obj is not null && await CanReadAsync(user, ctx, ctx.Product.Id, id, ct)) roots.Add(obj);
        }
        return new(source, card, at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), roots);
    }

    public async Task<ProductLevelDto> ChildrenAsync(ClaimsPrincipal user, string source, long cardId,
        long rootId, string? path, string? date, long after, CancellationToken ct)
    {
        ValidateCursor(after);
        var links = ProductExplorerRules.ParsePath(path, Math.Clamp(_options.MaxDepth, 1, 32));
        var at = ProductExplorerRules.ParseDate(date);
        await using var ctx = await OpenAsync(user, source, ct);
        var location = await LocateAsync(user, ctx, cardId, rootId, links, at, ct);
        if (links.Length >= Math.Clamp(_options.MaxDepth, 1, 32))
            throw new ProductExplorerProblem(409, "Достигнут предел глубины. Дальнейшее раскрытие отключено конфигурацией.");
        var page = await ScanAsync(after, async (cursor, take) =>
        {
            var rows = await ReadOccurrencesAsync(ctx, location.Object.ObjectId, at, cursor, take, null, ct);
            return rows.Select(row =>
            {
                var nodePath = links.Append(row.LinkId).ToArray();
                var state = location.Ancestors.Contains(row.Object.ObjectId) ? "cycle" :
                    row.InsertedStructureId != 0 ? "unsupported_structure" :
                    nodePath.Length >= Math.Clamp(_options.MaxDepth, 1, 32) ? "depth_limit" : "available";
                return new ProductOccurrenceDto(
                    ProductExplorerRules.NodeKey(source, ctx.Product.Id, rootId, nodePath), row.RowId,
                    row.LinkId, location.Object.ObjectId, row.Object, nodePath, row.Amount, row.Quantity,
                    row.Position, row.BomSection, row.Unit, state);
            }).ToList();
        }, x => x.RowId, x => CanReadAsync(user, ctx, ctx.Product.Id, x.Object.ObjectId, ct), ct);
        return new(source, rootId, at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), location.Object, links, page);
    }

    public async Task<ProductFileRootsDto> FileRootsAsync(ClaimsPrincipal user, string source, long cardId,
        long rootId, string category, string? path, string? date, CancellationToken ct)
    {
        var links = ProductExplorerRules.ParsePath(path, Math.Clamp(_options.MaxDepth, 1, 32));
        await using var ctx = await OpenAsync(user, source, ct);
        var location = await LocateAsync(user, ctx, cardId, rootId, links, ProductExplorerRules.ParseDate(date), ct);
        return await FileRootsCoreAsync(user, ctx, location.Object, category, ct);
    }

    public async Task<ProductFilePageDto> FilesAsync(ClaimsPrincipal user, string source, long cardId,
        long rootId, string category, string? path, string? folderPath, string? date, long after, CancellationToken ct)
    {
        ValidateCursor(after);
        var links = ProductExplorerRules.ParsePath(path, Math.Clamp(_options.MaxDepth, 1, 32));
        var folders = ProductExplorerRules.ParsePath(folderPath, Math.Clamp(_options.MaxDepth, 1, 32));
        if (folders.Length == 0) throw new ArgumentException("Не выбрана файловая папка.");
        await using var ctx = await OpenAsync(user, source, ct);
        var location = await LocateAsync(user, ctx, cardId, rootId, links, ProductExplorerRules.ParseDate(date), ct);
        var roots = await FileRootsCoreAsync(user, ctx, location.Object, category, ct);
        var folder = roots.Roots.SingleOrDefault(f => f.ObjectId == folders[0]);
        if (folder is null) throw NotFound();
        for (var i = 0; i < folders.Length; i++)
        {
            if (i > 0)
            {
                var row = await ReadFileAsync(ctx, folders[i], ct);
                if (row is null || row.ParentId != folders[i - 1]) throw NotFound();
                if (!await CanReadAsync(user, ctx, ctx.Files.Id, row.Item.ObjectId, ct)) throw Forbidden();
                folder = row.Item;
            }
            if (folder.Kind != "folder")
                throw new ProductExplorerProblem(409, "Тип папки не подтверждён файловым профилем источника.");
        }
        var page = await ScanAsync(after, async (cursor, take) =>
        {
            await using var cmd = Command(ctx.Cn, $@"
SELECT TOP (@take) f.s_ObjectID, f.s_Version, f.Name, f.Size, f.s_ClassID, f.s_ParentID
FROM dbo.{ProductSql.Q(ctx.Files.Table)} f
WHERE {ProductSql.Saved("f")} AND f.s_ParentID = @parent AND f.s_ObjectID > @after
ORDER BY f.s_ObjectID;");
            Add(cmd, "@parent", SqlDbType.BigInt, folder.ObjectId);
            Add(cmd, "@after", SqlDbType.BigInt, cursor); Add(cmd, "@take", SqlDbType.Int, take);
            var list = new List<ProductFileDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(FileRow(ctx, reader).Item);
            if (list.Select(x => x.ObjectId).Distinct().Count() != list.Count) throw Ambiguous();
            return list;
        }, x => x.ObjectId, x => CanReadAsync(user, ctx, ctx.Files.Id, x.ObjectId, ct), ct);
        return new(category, Title(category), location.Object, folder, folders, page);
    }

    private async Task<ProductFileRootsDto> FileRootsCoreAsync(ClaimsPrincipal user, Context ctx,
        ProductObjectDto owner, string category, CancellationToken ct)
    {
        if (!ctx.FileRelations.TryGetValue(category, out var relation))
            throw new ArgumentException("Допустимые файловые разделы: PDF, DXF, CAD.");
        await RequireGroupAsync(user, ctx, ctx.Files.Id, false, ct);
        await LoadClassesAsync(ctx, ct);
        var ids = await TargetsAsync(user, ctx, relation, ctx.Product.Id, ctx.Files.Id, owner.ObjectId, ct);
        var roots = new List<ProductFileDto>();
        foreach (var id in ids)
        {
            var row = await ReadFileAsync(ctx, id, ct);
            if (row is null) throw new ProductExplorerProblem(409, "Назначенная файловая папка отсутствует в сохранённом состоянии.");
            if (await CanReadAsync(user, ctx, ctx.Files.Id, id, ct)) roots.Add(row.Item);
        }
        return new(category, Title(category), owner, ids.Count == 0 ? "unassigned" : roots.Count == 0 ? "restricted" : "assigned", roots);
    }

    private async Task<Location> LocateAsync(ClaimsPrincipal user, Context ctx, long cardId, long rootId,
        long[] links, DateTime at, CancellationToken ct)
    {
        await ReadCardAsync(user, ctx, cardId, ct);
        var ids = await TargetsAsync(user, ctx, ctx.CardProduct, ctx.Card.Id, ctx.Product.Id, cardId, ct);
        if (!ids.Contains(rootId)) throw NotFound();
        var obj = await ReadProductAsync(ctx, rootId, ct) ?? throw NotFound();
        if (!await CanReadAsync(user, ctx, ctx.Product.Id, rootId, ct)) throw Forbidden();
        var ancestors = new HashSet<long> { rootId };
        foreach (var link in links)
        {
            var rows = await ReadOccurrencesAsync(ctx, obj.ObjectId, at, 0, 2, link, ct);
            if (rows.Count == 0) throw NotFound();
            if (rows.Count != 1) throw Ambiguous();
            var row = rows[0];
            if (!ancestors.Add(row.Object.ObjectId)) throw new ProductExplorerProblem(409, "Обнаружен цикл в составе.");
            if (row.InsertedStructureId != 0)
                throw new ProductExplorerProblem(409, "Вставленная структура требует отдельного сопоставления. Она не раскрывается как обычный состав.");
            if (!await CanReadAsync(user, ctx, ctx.Product.Id, row.Object.ObjectId, ct)) throw Forbidden();
            obj = row.Object;
        }
        return new(obj, ancestors);
    }

    private async Task<List<Occurrence>> ReadOccurrencesAsync(Context ctx, long parentId, DateTime at,
        long after, int take, long? linkId, CancellationToken ct)
    {
        await using var cmd = Command(ctx.Cn, $@"
SELECT TOP (@take) h.s_PK, h.s_HierarchyLinkID, h.s_InsertedStructureID,
       h.Amount, h.Quantity, h.Position, h.BOMSection, h.Unit,
       n.s_ObjectID, n.s_Guid, n.Denotation, n.Name, n.[Object], n.s_Version
FROM dbo.{ProductSql.Q(ctx.Hierarchy.Table)} h
JOIN dbo.{ProductSql.Q(ctx.Product.Table)} n ON n.s_ObjectID = h.s_ObjectID
WHERE h.s_ParentID = @parent AND h.s_PK > @after
  AND (@link IS NULL OR h.s_HierarchyLinkID = @link)
  AND {ProductSql.HierarchyFilter(ctx.Profile)}
ORDER BY h.s_PK;");
        Add(cmd, "@parent", SqlDbType.BigInt, parentId); Add(cmd, "@after", SqlDbType.BigInt, after);
        Add(cmd, "@take", SqlDbType.Int, take); Add(cmd, "@link", SqlDbType.BigInt, linkId);
        Add(cmd, "@asOf", SqlDbType.DateTime2, at);
        Add(cmd, "@structure", SqlDbType.Int, ctx.Profile.ProductStructureId);
        Add(cmd, "@design", SqlDbType.Int, ctx.Profile.DesignContextId);
        for (var i = 0; i < ctx.Profile.StructureTypeIds.Length; i++) Add(cmd, "@st" + i, SqlDbType.Int, ctx.Profile.StructureTypeIds[i]);
        for (var i = 0; i < ctx.Profile.ExcludedProductClassIds.Length; i++) Add(cmd, "@ec" + i, SqlDbType.Int, ctx.Profile.ExcludedProductClassIds[i]);
        var list = new List<Occurrence>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new(Long(reader, 0), Long(reader, 1), Int(reader, 2), NullableDouble(reader, 3),
                NullableInt(reader, 4), NullableInt(reader, 5), Text(reader, 6), Text(reader, 7), ProductRow(reader, 8)));
        if (list.Select(x => x.LinkId).Distinct().Count() != list.Count) throw Ambiguous();
        return list;
    }

    private async Task<ProductCardDto> ReadCardAsync(ClaimsPrincipal user, Context ctx, long id, CancellationToken ct)
    {
        if (id <= 0) throw new ArgumentException("Некорректная карточка.");
        await RequireGroupAsync(user, ctx, ctx.Card.Id, false, ct);
        if (!await CanReadAsync(user, ctx, ctx.Card.Id, id, ct)) throw Forbidden();
        await using var cmd = Command(ctx.Cn, $@"
SELECT TOP (2) n.s_ObjectID, n.Obekt, n.Name FROM dbo.{ProductSql.Q(ctx.Card.Table)} n
WHERE n.s_ObjectID = @id AND {ProductSql.Saved("n")};");
        Add(cmd, "@id", SqlDbType.BigInt, id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw NotFound();
        var result = new ProductCardDto(Long(reader, 0), Text(reader, 1), Text(reader, 2));
        if (await reader.ReadAsync(ct)) throw Ambiguous();
        return result;
    }

    private async Task<ProductObjectDto?> ReadProductAsync(Context ctx, long id, CancellationToken ct)
    {
        await using var cmd = Command(ctx.Cn, $@"
SELECT TOP (2) n.s_ObjectID, n.s_Guid, n.Denotation, n.Name, n.[Object], n.s_Version
FROM dbo.{ProductSql.Q(ctx.Product.Table)} n
WHERE n.s_ObjectID = @id AND {ProductSql.Saved("n")};");
        Add(cmd, "@id", SqlDbType.BigInt, id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var result = ProductRow(reader, 0);
        if (await reader.ReadAsync(ct)) throw Ambiguous();
        return result;
    }

    private async Task<List<long>> TargetsAsync(ClaimsPrincipal user, Context ctx, ProductReference relation,
        int sourceGroup, int targetGroup, long objectId, CancellationToken ct)
    {
        var side = ProductExplorerRules.ResolveSide(relation, sourceGroup, targetGroup);
        var policies = await RelationPoliciesAsync(user, ctx, sourceGroup, ct);
        if (!policies.Any(p => p.RelationTable.Equals(relation.Table, StringComparison.OrdinalIgnoreCase)
            && p.CanTraverse && ProductExplorerRules.DirectionAllowed(side, p.DirectionMode))) throw Forbidden();
        await RequireGroupAsync(user, ctx, targetGroup, false, ct);
        var max = Math.Clamp(_options.MaxRoots, 1, 100);
        await using var cmd = Command(ctx.Cn, $@"
SELECT DISTINCT TOP (@take) l.{ProductSql.Q(side.TargetColumn)},
       CASE WHEN l.[Context] IS NULL OR DATALENGTH(l.[Context]) = 0 THEN 0 ELSE 1 END AS HasContext
FROM dbo.{ProductSql.Q(relation.Table)} l
WHERE l.{ProductSql.Q(side.SourceColumn)} = @id AND {ProductSql.ActiveRelation("l")}
ORDER BY l.{ProductSql.Q(side.TargetColumn)};");
        Add(cmd, "@take", SqlDbType.Int, max + 1); Add(cmd, "@id", SqlDbType.BigInt, objectId);
        var result = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Int(reader, 1) != 0) throw new ProductExplorerProblem(409, "Связь содержит контекст. Её нельзя раскрывать как обычное назначение папки или изделия.");
            result.Add(Long(reader, 0));
        }
        if (result.Count > max) throw new ProductExplorerProblem(409, "Число назначенных корней превышает лимит. Результат не обрезан молча.");
        return result;
    }

    private async Task<FileRecord?> ReadFileAsync(Context ctx, long id, CancellationToken ct)
    {
        await using var cmd = Command(ctx.Cn, $@"
SELECT TOP (2) f.s_ObjectID, f.s_Version, f.Name, f.Size, f.s_ClassID, f.s_ParentID
FROM dbo.{ProductSql.Q(ctx.Files.Table)} f WHERE f.s_ObjectID = @id AND {ProductSql.Saved("f")};");
        Add(cmd, "@id", SqlDbType.BigInt, id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var row = FileRow(ctx, reader);
        if (await reader.ReadAsync(ct)) throw Ambiguous();
        return row;
    }

    private async Task LoadClassesAsync(Context ctx, CancellationToken ct)
    {
        if (ctx.Folders is not null) return;
        await using var cmd = Command(ctx.Cn, "SELECT PK, BaseClassID, GroupID FROM dbo.Classes WHERE GroupID = @group;");
        Add(cmd, "@group", SqlDbType.Int, ctx.Files.Id);
        var classes = new List<ProductClass>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) classes.Add(new(Int(reader, 0), Int(reader, 1), Int(reader, 2)));
        ctx.Folders = ProductExplorerRules.ExpandClasses(classes, ctx.Profile.FolderClassIds, ctx.Files.Id);
        ctx.FileClasses = ProductExplorerRules.ExpandClasses(classes, ctx.Profile.FileClassIds, ctx.Files.Id);
        if (ctx.Folders.Overlaps(ctx.FileClasses)) throw new ProductExplorerProblem(409, "Классы папок и файлов пересекаются в профиле источника.");
    }

    private async Task RequireGroupAsync(ClaimsPrincipal user, Context ctx, int id, bool search, CancellationToken ct)
    {
        if (!ctx.GroupAccess.TryGetValue(id, out var policy))
        {
            policy = await _access.GetGroupAccessAsync(user, ctx.Source, id, ct);
            ctx.GroupAccess[id] = policy;
        }
        if (policy is null || (search ? !policy.CanSearch : !policy.CanOpenCard)) throw Forbidden();
    }

    private async Task<bool> CanReadAsync(ClaimsPrincipal user, Context ctx, int group, long id, CancellationToken ct)
    {
        await RequireGroupAsync(user, ctx, group, false, ct);
        if (!_decisions.ShouldEnforceTflexRead) return true;
        if (ctx.Decisions.TryGetValue((group, id), out var allowed)) return allowed;
        var result = await _decisions.CanReadAsync(user, ctx.Source, group, id, ct);
        ctx.Decisions[(group, id)] = result.Allowed;
        return result.Allowed;
    }

    private async Task<IReadOnlyList<EffectiveRelationAccessDto>> RelationPoliciesAsync(ClaimsPrincipal user,
        Context ctx, int group, CancellationToken ct)
    {
        if (!ctx.Relations.TryGetValue(group, out var policies))
        {
            policies = await _access.GetAllowedRelationsAsync(user, ctx.Source, group, ct);
            ctx.Relations[group] = policies;
        }
        return policies;
    }

    private async Task<Context> OpenAsync(ClaimsPrincipal user, string source, CancellationToken ct)
    {
        var entry = _options.Sources.FirstOrDefault(x => x.Key.Equals(source, StringComparison.OrdinalIgnoreCase));
        var profile = entry.Value ?? throw NotFound();
        source = entry.Key;
        if (string.IsNullOrWhiteSpace(profile.ExpectedDatabase) || string.IsNullOrWhiteSpace(profile.ExpectedServer))
            throw new ProductExplorerProblem(503, "Для источника не задан проверенный сервер и база.");
        if (profile.ProductStructureId != 0 || profile.DesignContextId != 0)
            throw new ProductExplorerProblem(409, "Этот этап поддерживает только основной сохранённый состав без контекста проектирования.");
        _ = ProductSql.HierarchyFilter(profile);
        var appUser = await _access.EnsureAppUserAsync(user, ct);
        await using (var app = await _factory.OpenAppConnectionAsync(ct))
        await using (var cmd = Command(app, @"
SELECT COUNT_BIG(*) FROM app.Source s
WHERE s.Code = @source AND s.IsEnabled = 1
AND EXISTS (SELECT 1 FROM app.AppUser u WHERE u.AppUserId = @user AND u.IsEnabled = 1);"))
        {
            Add(cmd, "@source", SqlDbType.NVarChar, source, 100); Add(cmd, "@user", SqlDbType.Int, appUser);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1) throw Forbidden();
        }
        var cn = await _factory.OpenSourceConnectionAsync(source, ct);
        try
        {
            await using (var cmd = Command(cn, "SELECT CONVERT(nvarchar(128), SERVERPROPERTY(N'ServerName')), DB_NAME();"))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)
                    || !string.Equals(Text(reader, 0), profile.ExpectedServer, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Text(reader, 1), profile.ExpectedDatabase, StringComparison.OrdinalIgnoreCase))
                    throw new ProductExplorerProblem(409, "Подключение ведёт не в проверенный сервер/базу. Загрузка остановлена.");
            }
            var refs = await ReadMetadataAsync(cn, profile, ct);
            ProductReference Get(string table, int type)
            {
                var matches = refs.Where(x => x.Table.Equals(table, StringComparison.OrdinalIgnoreCase) && x.Type == type).ToArray();
                if (matches.Length != 1) throw new ProductExplorerProblem(409, "Метаданные маршрута отсутствуют или неоднозначны. Проверьте профиль источника.");
                return matches[0];
            }
            var card = Get(profile.CardTable, 1); var product = Get(profile.ProductTable, 1);
            var hierarchy = Get(profile.HierarchyTable, 4); var files = Get(profile.FilesTable, 1);
            if (hierarchy.MasterId != product.Id) throw new ProductExplorerProblem(409, "Иерархия не принадлежит справочнику изделия.");
            var cardProduct = Get(profile.CardProductRelation, 5);
            _ = ProductExplorerRules.ResolveSide(cardProduct, card.Id, product.Id);
            var fileRelations = new Dictionary<string, ProductReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in new[] { "pdf", "dxf", "cad" })
            {
                if (!profile.FileRelations.TryGetValue(code, out var table)) throw new ProductExplorerProblem(409, "В профиле не задан файловый маршрут.");
                var relation = Get(table, 5); _ = ProductExplorerRules.ResolveSide(relation, product.Id, files.Id);
                fileRelations[code] = relation;
            }
            return new Context(cn, source, profile, card, product, hierarchy, files, cardProduct, fileRelations);
        }
        catch { await cn.DisposeAsync(); throw; }
    }

    private async Task<List<ProductReference>> ReadMetadataAsync(SqlConnection cn, ProductSourceProfile p, CancellationToken ct)
    {
        var tables = new[] { p.CardTable, p.ProductTable, p.HierarchyTable, p.FilesTable, p.CardProductRelation }
            .Concat(p.FileRelations.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var table in tables) _ = ProductSql.Q(table);
        await using var cmd = Command(cn, $@"
SELECT PK, CONVERT(nvarchar(36), Guid), TableName, Caption, [Type], MasterGroupID, SlaveGroupID
FROM dbo.ParameterGroups WHERE TableName IN ({string.Join(",", tables.Select((_, i) => "@t" + i))});");
        for (var i = 0; i < tables.Length; i++) Add(cmd, "@t" + i, SqlDbType.NVarChar, tables[i], 128);
        var list = new List<ProductReference>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new(Int(reader, 0), Text(reader, 1) ?? "", Text(reader, 2)!,
            Text(reader, 3) ?? "", Int(reader, 4), Int(reader, 5), Int(reader, 6)));
        return list;
    }

    private async Task<ProductPageDto<T>> ScanAsync<T>(long after, Func<long, int, Task<List<T>>> read,
        Func<T, long> key, Func<T, Task<bool>> allowed, CancellationToken ct)
    {
        var pageSize = Math.Clamp(_options.PageSize, 1, 100);
        var budget = Math.Clamp(_options.MaxScanRows, pageSize + 1, 1000);
        var result = new List<T>(); var cursor = after; var scanned = 0;
        while (scanned < budget)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(pageSize + 1, budget - scanned);
            var rows = await read(cursor, take);
            foreach (var item in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (key(item) <= cursor) throw Ambiguous();
                if (await allowed(item))
                {
                    if (result.Count == pageSize) return new(result, cursor, true, false);
                    result.Add(item);
                }
                cursor = key(item); scanned++;
            }
            if (rows.Count < take) return new(result, null, false, false);
        }
        return new(result, cursor, true, true);
    }

    private SqlCommand Command(SqlConnection cn, string text)
    {
        var cmd = cn.CreateCommand(); cmd.CommandText = text;
        cmd.CommandTimeout = Math.Clamp(_options.CommandTimeoutSeconds, 1, 60); return cmd;
    }
    private static void Add(SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    {
        var p = size > 0 ? new SqlParameter(name, type, size) : new SqlParameter(name, type);
        p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p);
    }
    private static ProductObjectDto ProductRow(SqlDataReader r, int i) =>
        new(Long(r, i), Text(r, i + 1), Text(r, i + 2), Text(r, i + 3), Text(r, i + 4), Int(r, i + 5));
    private static FileRecord FileRow(Context ctx, SqlDataReader r) => new(
        new ProductFileDto(Long(r, 0), Int(r, 1), Text(r, 2), r.IsDBNull(3) ? null : Long(r, 3),
            ProductExplorerRules.ClassifyFile(Int(r, 4), ctx.Folders!, ctx.FileClasses!)), Long(r, 5));
    private static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    private static int Int(SqlDataReader r, int i) => Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
    private static long Long(SqlDataReader r, int i) => Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    private static int? NullableInt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Int(r, i);
    private static double? NullableDouble(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
    private static string Title(string category) => category.ToLowerInvariant() switch
    { "pdf" => "PDF", "dxf" => "DXF", "cad" => "CAD", _ => throw new ArgumentException("Неизвестный файловый раздел.") };
    private static void ValidateCursor(long cursor) { if (cursor < 0) throw new ArgumentException("Некорректное продолжение списка."); }
    private static ProductExplorerProblem Forbidden() => new(403, "Просмотр недоступен. Проверьте разрешение раздела и права выбранного пользователя.");
    private static ProductExplorerProblem NotFound() => new(404, "Объект не найден в выбранном источнике или пути.");
    private static ProductExplorerProblem Ambiguous() => new(409, "Найдены неоднозначные актуальные строки. Загрузка остановлена для проверки данных.");

    private sealed record Occurrence(long RowId, long LinkId, int InsertedStructureId, double? Amount,
        int? Quantity, int? Position, string? BomSection, string? Unit, ProductObjectDto Object);
    private sealed record FileRecord(ProductFileDto Item, long ParentId);
    private sealed record Location(ProductObjectDto Object, HashSet<long> Ancestors);
    private sealed class Context : IAsyncDisposable
    {
        public SqlConnection Cn { get; }
        public string Source { get; }
        public ProductSourceProfile Profile { get; }
        public ProductReference Card { get; }
        public ProductReference Product { get; }
        public ProductReference Hierarchy { get; }
        public ProductReference Files { get; }
        public ProductReference CardProduct { get; }
        public Dictionary<string, ProductReference> FileRelations { get; }
        public Dictionary<int, EffectiveGroupAccessDto?> GroupAccess { get; } = new();
        public Dictionary<int, IReadOnlyList<EffectiveRelationAccessDto>> Relations { get; } = new();
        public Dictionary<(int, long), bool> Decisions { get; } = new();
        public HashSet<int>? Folders { get; set; }
        public HashSet<int>? FileClasses { get; set; }
        public Context(SqlConnection cn, string source, ProductSourceProfile profile, ProductReference card,
            ProductReference product, ProductReference hierarchy, ProductReference files, ProductReference relation,
            Dictionary<string, ProductReference> fileRelations)
        {
            Cn = cn; Source = source; Profile = profile; Card = card; Product = product;
            Hierarchy = hierarchy; Files = files; CardProduct = relation; FileRelations = fileRelations;
        }
        public ValueTask DisposeAsync() => Cn.DisposeAsync();
    }
}
