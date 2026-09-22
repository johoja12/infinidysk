using System.Text.RegularExpressions;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Groups the indexed library by recognized link directories. No title or
/// episode metadata is inferred from release names or remote services.
/// </summary>
public sealed class LibraryBrowseService(LibraryCatalogService catalog)
{
    private const int GroupFilePageSize = 50;
    private static readonly Regex EpisodePattern = new(
        @"(?<![A-Za-z0-9])S(?<season>\d{1,2})E(?<episode>\d{1,3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<LibraryBrowseResult> QueryAsync(
        LibraryBrowseQuery query,
        LibraryCatalogScanner? scanner = null,
        CancellationToken ct = default)
    {
        var all = await catalog.LoadAllAsync(ct).ConfigureAwait(false);
        var matched = all
            .Where(i => string.IsNullOrWhiteSpace(query.Search)
                || LibraryCatalogService.MatchesSearch(i, query.Search.Trim()))
            .Where(i => query.TypeFilter switch
            {
                "internal" => i.Kind == "internal",
                "external" => i.Kind == "external",
                "broken" => i.Mappings.Any(m => m.Status is "broken" or "stale"),
                _ => true,
            })
            .Select(i => new ClassifiedItem(i, Classify(i)))
            .ToList();

        var selected = matched.Where(i => i.Identity.Category == query.Category);
        var groups = selected
            .GroupBy(i => i.Identity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.Key, g.First().Identity.Title,
                g.First().Identity.Category, g.Select(i => i.Item).ToList()))
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var pageSize = Math.Clamp(query.PageSize, 1, 24);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (groups.Count + pageSize - 1) / pageSize));
        var pageGroups = groups.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(g => new LibraryBrowseGroupDto(g.Key, g.Title, g.Category,
                g.Items.Count, g.Items.Count(IsHealthy), g.Items.Count(NeedsAttention)))
            .ToList();

        LibraryBrowseExpandedGroupDto? expanded = null;
        var expandedGroup = groups.FirstOrDefault(g =>
            string.Equals(g.Key, query.GroupKey, StringComparison.OrdinalIgnoreCase));
        if (expandedGroup is not null)
        {
            var groupPage = Math.Clamp(query.GroupPage, 1,
                Math.Max(1, (expandedGroup.Items.Count + GroupFilePageSize - 1) / GroupFilePageSize));
            var files = expandedGroup.Items
                .OrderBy(i => EpisodeSortKey(i))
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Skip((groupPage - 1) * GroupFilePageSize)
                .Take(GroupFilePageSize)
                .Select(i =>
                {
                    var episode = EpisodeMatch(i);
                    return new LibraryBrowseFileDto(i,
                        episode.Success ? $"Season {int.Parse(episode.Groups["season"].Value)}" : null,
                        episode.Success
                            ? $"S{int.Parse(episode.Groups["season"].Value):00}E{int.Parse(episode.Groups["episode"].Value):00}"
                            : null);
                })
                .ToList();
            expanded = new LibraryBrowseExpandedGroupDto(expandedGroup.Key,
                groupPage, GroupFilePageSize, expandedGroup.Items.Count, files);
        }

        return new LibraryBrowseResult(pageGroups, groups.Count, page, pageSize,
            matched.Count, matched.Count(i => IsHealthy(i.Item)),
            matched.Count(i => NeedsAttention(i.Item)),
            matched.Count(i => i.Identity.Category == "unmatched"), expanded,
            scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning);
    }

    private static bool IsHealthy(LibraryCatalogItemDto item) =>
        item.Health == "healthy";

    private static bool NeedsAttention(LibraryCatalogItemDto item) =>
        item.Health is "attention" or "unmapped";

    private static string EpisodeSortKey(LibraryCatalogItemDto item)
    {
        var match = EpisodeMatch(item);
        return match.Success
            ? $"{int.Parse(match.Groups["season"].Value):D3}/{int.Parse(match.Groups["episode"].Value):D4}"
            : "999/9999";
    }

    private static string? PrimaryLinkPath(LibraryCatalogItemDto item) =>
        item.Mappings.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.LinkPath;

    private static Match EpisodeMatch(LibraryCatalogItemDto item)
    {
        foreach (var mapping in item.Mappings.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase))
        {
            var match = EpisodePattern.Match(mapping.LinkPath);
            if (match.Success) return match;
        }
        return EpisodePattern.Match(item.DisplayName);
    }

    private static GroupIdentity Classify(LibraryCatalogItemDto item)
    {
        if (item.Kind == "internal")
        {
            foreach (var mapping in item.Mappings
                .Where(m => m.MappingType == "internal")
                .OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase))
            {
                var segments = mapping.LinkPath.Replace('\\', '/').Split('/',
                    StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < Math.Min(3, segments.Length - 1); index++)
                {
                    var root = segments[index].ToLowerInvariant();
                    var category = root switch
                    {
                        "tv" or "shows" or "tv shows" or "series" or "television" => "shows",
                        "movies" or "films" => "movies",
                        _ => null,
                    };
                    if (category is null) continue;
                    var title = segments[index + 1];
                    if (category == "shows" && index + 2 >= segments.Length)
                        continue; // A bare file under TV is not a known show.
                    if (category == "movies" && index + 2 >= segments.Length)
                        title = Path.GetFileNameWithoutExtension(title);
                    if (!string.IsNullOrWhiteSpace(title))
                        return new GroupIdentity(category, $"{category}/{title}", title);
                }
            }
        }
        var key = item.DavItemId?.ToString("D") ?? PrimaryLinkPath(item) ?? item.DisplayName;
        return new GroupIdentity("unmatched", $"unmatched/{key}", item.DisplayName);
    }

    private sealed record GroupIdentity(string Category, string Key, string Title);
    private sealed record ClassifiedItem(LibraryCatalogItemDto Item, GroupIdentity Identity);
    private sealed record Group(string Key, string Title, string Category,
        IReadOnlyList<LibraryCatalogItemDto> Items);
}
