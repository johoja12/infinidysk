namespace NzbWebDAV.Services.Library;

public sealed record LibraryCatalogQuery
{
    public string? Search { get; init; }
    public string TypeFilter { get; init; } = "all"; // all|internal|external|broken
    public string Sort { get; init; } = "name"; // name|size|mappings
    public string Direction { get; init; } = "asc"; // asc|desc
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record LibraryCatalogMappingDto(
    string LinkPath,
    string TargetText,
    string MappingType,
    string Status);

public sealed record LibraryCatalogItemDto(
    string Kind, // internal|external
    Guid? DavItemId,
    string DisplayName,
    string? ContentPath,
    long? Size,
    int MappingCount,
    string Health,
    IReadOnlyList<LibraryCatalogMappingDto> Mappings);

public sealed record LibraryCatalogResult(
    IReadOnlyList<LibraryCatalogItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    DateTimeOffset? IndexScannedAt,
    string? IndexWarning);

public sealed record LibraryBrowseQuery
{
    public string? Search { get; init; }
    public string Category { get; init; } = "shows"; // shows|movies|unmatched
    public string TypeFilter { get; init; } = "all"; // all|internal|external|broken
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 12;
    public string? GroupKey { get; init; }
    public int GroupPage { get; init; } = 1;
}

public sealed record LibraryBrowseGroupDto(
    string Key,
    string Title,
    string Category,
    int ItemCount,
    int HealthyCount,
    int AttentionCount);

public sealed record LibraryBrowseFileDto(
    LibraryCatalogItemDto Item,
    string? Season,
    string? Episode);

public sealed record LibraryBrowseExpandedGroupDto(
    string Key,
    int Page,
    int PageSize,
    int TotalItems,
    IReadOnlyList<LibraryBrowseFileDto> Items);

public sealed record LibraryBrowseResult(
    IReadOnlyList<LibraryBrowseGroupDto> Groups,
    int TotalGroups,
    int Page,
    int PageSize,
    int TotalItems,
    int HealthyItems,
    int AttentionItems,
    int UnmatchedItems,
    LibraryBrowseExpandedGroupDto? ExpandedGroup,
    DateTimeOffset? IndexScannedAt,
    string? IndexWarning);
