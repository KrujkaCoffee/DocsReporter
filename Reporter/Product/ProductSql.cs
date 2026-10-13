namespace DocsApi.Reporter.Product;

// Чистые фрагменты проверяются отдельно. Имена таблиц приходят только из серверного профиля и ParameterGroups.
public static class ProductSql
{
    public static string Q(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 128)
            throw new ArgumentException("Некорректное имя таблицы или поля.");
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    public static string Saved(string alias) =>
        $"{alias}.s_ActualVersion = 1 AND {alias}.s_ClientViewID = 0 AND {alias}.s_Deleted = 0 AND {alias}.s_State = 0";

    public static string ActiveRelation(string alias) =>
        $"{alias}.DeletedChangelistID = 0 AND {alias}.AddedClientViewID = 0";

    public static string HierarchyFilter(ProductSourceProfile profile)
    {
        if (profile.StructureTypeIds.Length == 0 || profile.StructureTypeIds.Length > 64 || profile.StructureTypeIds.Any(x => x <= 0))
            throw new InvalidOperationException("Не задан проверенный набор видов структуры.");
        if (profile.ExcludedProductClassIds.Length > 128 || profile.ExcludedProductClassIds.Any(x => x <= 0))
            throw new InvalidOperationException("Некорректный список исключаемых классов.");
        var types = string.Join(",", profile.StructureTypeIds.Select((_, i) => "@st" + i));
        var excluded = profile.ExcludedProductClassIds.Length == 0 ? "" :
            " AND n.s_ClassID NOT IN (" + string.Join(",", profile.ExcludedProductClassIds.Select((_, i) => "@ec" + i)) + ")";
        return $@"{Saved("h")} AND {Saved("n")}
AND h.s_ProductStructureID = @structure AND h.s_DesignContextId = @design
AND h.s_DeletedInDesignContext = 0
AND (h.s_StartDate IS NULL OR h.s_StartDate <= @asOf)
AND (h.s_EndDate IS NULL OR h.s_EndDate > @asOf)
AND EXISTS (SELECT 1 FROM dbo.UsingInStructures u
            WHERE u.NomenclatureLinkId = h.s_HierarchyLinkID AND u.StructureTypeId IN ({types})){excluded}";
    }
}
