using System.Globalization;

namespace DocsApi.Reporter.Product;

public sealed record ProductReference(int Id, string Guid, string Table, string Caption, int Type, int MasterId, int SlaveId);
public sealed record ProductRelationSide(string SourceColumn, string TargetColumn, int TargetGroupId);
public sealed record ProductClass(int Id, int BaseId, int GroupId);

public static class ProductExplorerRules
{
    public static ProductRelationSide ResolveSide(ProductReference relation, int sourceGroupId, int targetGroupId)
    {
        if (relation.Type != 5 || sourceGroupId == targetGroupId)
            throw new InvalidOperationException("Для этого маршрута требуется однозначная межсправочная связь.");
        if (relation.MasterId == sourceGroupId && relation.SlaveId == targetGroupId)
            return new("MasterID", "SlaveID", targetGroupId);
        if (relation.SlaveId == sourceGroupId && relation.MasterId == targetGroupId)
            return new("SlaveID", "MasterID", targetGroupId);
        throw new InvalidOperationException("Стороны связи не совпадают с выбранными справочниками источника.");
    }

    public static bool DirectionAllowed(ProductRelationSide side, string policyDirection) => policyDirection switch
    {
        "Both" => true,
        "MasterToSlave" => side.SourceColumn == "MasterID",
        "SlaveToMaster" => side.SourceColumn == "SlaveID",
        _ => false
    };

    public static long[] ParsePath(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<long>();
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > limit) throw new ArgumentException("Достигнут предел глубины раскрытия.");
        var result = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]) || result[i] <= 0)
                throw new ArgumentException("Некорректный путь объекта.");
        if (result.Distinct().Count() != result.Length) throw new ArgumentException("В пути обнаружен цикл.");
        return result;
    }

    public static DateTime ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return DateTime.Today;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) throw new ArgumentException("Некорректная дата состава.");
        return parsed.Date;
    }

    public static bool AppliesAt(DateTime? start, DateTime? end, DateTime at) =>
        (!start.HasValue || start.Value <= at) && (!end.HasValue || end.Value > at);

    public static string EscapeLike(string text) => text.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_").Replace("[", "~[");

    public static HashSet<int> ExpandClasses(IReadOnlyList<ProductClass> classes, IEnumerable<int> roots, int groupId)
    {
        var byId = classes.Where(x => x.GroupId == groupId).ToDictionary(x => x.Id);
        var ids = roots.ToHashSet();
        if (ids.Count == 0 || ids.Any(id => !byId.ContainsKey(id)))
            throw new InvalidOperationException("Классы файлового профиля не подтверждены в выбранном источнике.");
        bool changed;
        do
        {
            changed = false;
            foreach (var item in byId.Values)
                if (ids.Contains(item.BaseId)) changed |= ids.Add(item.Id);
        } while (changed);
        return ids;
    }

    public static string ClassifyFile(int classId, IReadOnlySet<int> folders, IReadOnlySet<int> files) =>
        folders.Contains(classId) ? "folder" : files.Contains(classId) ? "file" : "unknown";

    public static string NodeKey(string source, int groupId, long rootId, IEnumerable<long> path) =>
        $"{Uri.EscapeDataString(source.ToLowerInvariant())}:{groupId}:{rootId}:{string.Join(",", path)}";
}
