namespace DocsApi.Reporter.Product;

public sealed class ProductExplorerOptions
{
    public bool Enabled { get; set; }
    public bool AllowAnonymousInDevelopment { get; set; }
    public int PageSize { get; set; } = 50;
    public int MaxDepth { get; set; } = 16;
    public int MaxRoots { get; set; } = 20;
    public int MaxScanRows { get; set; } = 500;
    public int CommandTimeoutSeconds { get; set; } = 20;
    public Dictionary<string, ProductSourceProfile> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProductSourceProfile
{
    public string ExpectedServer { get; set; } = "";
    public string ExpectedDatabase { get; set; } = "";
    public string CardTable { get; set; } = "Kartochka_proekta";
    public string ProductTable { get; set; } = "Nomenclature";
    public string HierarchyTable { get; set; } = "NomenclatureHierarchy";
    public string FilesTable { get; set; } = "Files";
    public string CardProductRelation { get; set; } = "Link_891_403_1";
    public Dictionary<string, string> FileRelations { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "Link_403_16_1_1",
        ["dxf"] = "Link_403_16_2",
        ["cad"] = "Link_403_16_1"
    };
    public int[] FolderClassIds { get; set; } = Array.Empty<int>();
    public int[] FileClassIds { get; set; } = Array.Empty<int>();
    public int[] StructureTypeIds { get; set; } = Array.Empty<int>();
    public int[] ExcludedProductClassIds { get; set; } = Array.Empty<int>();
    public int ProductStructureId { get; set; }
    public int DesignContextId { get; set; }
}
