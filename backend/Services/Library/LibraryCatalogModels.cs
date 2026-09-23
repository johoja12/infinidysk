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

public sealed record LibraryBrowseFile(LibraryCatalogItemDto Item, string? EpisodeLabel);

public sealed record LibraryBrowseGroup(
    string Key,
    string Kind,
    string Name,
    IReadOnlyList<LibraryBrowseFile> Files,
    int FileCount,
    int MappingCount,
    long TotalSize,
    int AttentionCount);

public sealed record LibraryBrowseResult(
    IReadOnlyList<LibraryBrowseGroup> Groups,
    int TotalGroups,
    int TotalFiles,
    int ShowCount,
    int MovieCount,
    int UnmatchedCount,
    int Page,
    int PageSize,
    DateTimeOffset? IndexScannedAt,
    string? IndexWarning);
