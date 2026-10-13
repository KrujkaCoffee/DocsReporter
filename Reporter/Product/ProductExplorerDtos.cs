namespace DocsApi.Reporter.Product;

public sealed record ProductSourceDto(string Code, string DisplayName);
public sealed record ProductCardDto(long ObjectId, string? Code, string? Name);
public sealed record ProductPageDto<T>(IReadOnlyList<T> Items, long? NextCursor, bool HasMore, bool ScanLimited);
public sealed record ProductObjectDto(long ObjectId, string? Guid, string? Denotation, string? Name, string? DisplayText, int Version);
public sealed record ProductCardRootsDto(string SourceCode, ProductCardDto Card, string AsOf, IReadOnlyList<ProductObjectDto> Roots);
public sealed record ProductOccurrenceDto(
    string NodeKey, long RowId, long HierarchyLinkId, long ParentObjectId,
    ProductObjectDto Object, IReadOnlyList<long> Path, double? Amount, int? Quantity,
    int? Position, string? BomSection, string? Unit, string ExpansionState);
public sealed record ProductLevelDto(string SourceCode, long RootId, string AsOf, ProductObjectDto Parent,
    IReadOnlyList<long> Path, ProductPageDto<ProductOccurrenceDto> Page);
public sealed record ProductFileDto(long ObjectId, int Version, string? Name, long? Size, string Kind);
public sealed record ProductFileRootsDto(string Category, string Title, ProductObjectDto Owner,
    string State, IReadOnlyList<ProductFileDto> Roots);
public sealed record ProductFilePageDto(string Category, string Title, ProductObjectDto Owner,
    ProductFileDto Folder, IReadOnlyList<long> FolderPath, ProductPageDto<ProductFileDto> Page);
