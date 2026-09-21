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
