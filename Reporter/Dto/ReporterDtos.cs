namespace DocsApi.Reporter.Dto;

public sealed record SourceDto(
    int SourceId,
    string Code,
    string DisplayName,
    string? BaseDocsUrl,
    bool IsEnabled);

public sealed record EffectiveGroupAccessDto(
    bool CanSeeInMenu,
    bool CanSearch,
    bool CanOpenCard,
    bool CanExport,
    int MaxObjectDepth,
    int MaxFileTreeDepth,
    int MaxRowsPerPage,
    int MaxRelatedObjects);

public sealed record EffectiveRelationAccessDto(
    int? RelationGroupId,
    string RelationTable,
    string? DisplayName,
    string? CategoryCode,
    string? CategoryTitle,
    bool CanTraverse,
    bool ShowInCard,
    bool ShowInTree,
    int MaxDepth,
    int MaxItems,
    string DirectionMode);

public sealed record ProjectCardSearchItemDto(
    string SourceCode,
    long ObjectId,
    string? Guid,
    string? ObjectCode,
    string? Name,
    string? DocsUrl);

public sealed record ObjectPreviewDto(
    int? GroupId,
    string? TableName,
    long ObjectId,
    string? Guid,
    string? ObjectCode,
    string? Name,
    string? DocsUrl);

public sealed record RelationItemDto(
    string RelationTable,
    string? DisplayName,
    string? CategoryCode,
    string? CategoryTitle,
    string Direction,
    long MasterId,
    long SlaveId,
    ObjectPreviewDto? Target);

public sealed record ProjectCardFullDto(
    string SourceCode,
    int GroupId,
    ObjectPreviewDto Card,
    IReadOnlyList<RelationItemDto> Relations,
    int RequestedDepth,
    int EffectiveDepth,
    IReadOnlyDictionary<string, object?> TechnicalTrace);

public sealed record DiscoveryResultDto(
    string SourceCode,
    int GroupsUpserted,
    int RelationsUpserted);

public sealed record BootstrapResultDto(
    string SourceCode,
    int GroupPoliciesUpserted,
    int RelationPoliciesUpserted);
