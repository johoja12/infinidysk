using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Classifies indexed files by their Plex filename match, then groups matched
/// files using the recognized link directories.
/// </summary>
public sealed class LibraryBrowseService(LibraryCatalogService catalog, IPlexLibraryMetadataIndex plexMetadata)
{
    private const int GroupFilePageSize = 50;
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
            .Select(i =>
            {
                var plex = Match(i, plexMetadata);
                return new ClassifiedItem(i, Classify(i, plex), plex);
            })
            .ToList();

        var selected = plexMetadata.Status.Ready
            ? matched.Where(i => i.Identity.Category == query.Category)
            : Enumerable.Empty<ClassifiedItem>();
        var groups = selected
            .GroupBy(i => i.Identity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.Key, g.First().Identity.Title,
                g.First().Identity.Category, g.ToList()))
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var pageSize = Math.Clamp(query.PageSize, 1, 24);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (groups.Count + pageSize - 1) / pageSize));
        var pageGroups = groups.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(g => new LibraryBrowseGroupDto(g.Key, g.Title, g.Category,
                g.Items.Count, g.Items.Count(i => IsHealthy(i.Item)), g.Items.Count(i => NeedsAttention(i.Item))))
            .ToList();

        LibraryBrowseExpandedGroupDto? expanded = null;
        var expandedGroup = groups.FirstOrDefault(g =>
            string.Equals(g.Key, query.GroupKey, StringComparison.OrdinalIgnoreCase));
        if (expandedGroup is not null)
        {
            var groupPage = Math.Clamp(query.GroupPage, 1,
                Math.Max(1, (expandedGroup.Items.Count + GroupFilePageSize - 1) / GroupFilePageSize));
            var files = expandedGroup.Items
                .OrderBy(i => i.PlexMatch?.Season ?? 999)
                .ThenBy(i => i.PlexMatch?.Episode ?? 9999)
                .ThenBy(i => i.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Skip((groupPage - 1) * GroupFilePageSize)
                .Take(GroupFilePageSize)
                .Select(i =>
                {
                    var season = i.PlexMatch?.Season;
                    var episode = i.PlexMatch?.Episode;
                    return new LibraryBrowseFileDto(i.Item,
                        season.HasValue ? $"Season {season}" : null,
                        season.HasValue && episode.HasValue ? $"S{season:00}E{episode:00}" : null);
                })
                .ToList();
            expanded = new LibraryBrowseExpandedGroupDto(expandedGroup.Key,
                groupPage, GroupFilePageSize, expandedGroup.Items.Count, files);
        }

        return new LibraryBrowseResult(pageGroups, groups.Count, page, pageSize,
            matched.Count, matched.Count(i => IsHealthy(i.Item)),
            matched.Count(i => NeedsAttention(i.Item)),
            plexMetadata.Status.Ready ? matched.Count(i => i.Identity.Category == "unmatched") : 0, expanded,
            scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning, plexMetadata.Status);
    }

    private static bool IsHealthy(LibraryCatalogItemDto item) =>
        item.Health == "healthy";

    private static bool NeedsAttention(LibraryCatalogItemDto item) =>
        item.Health is "attention" or "unmapped";

    private static string? PrimaryLinkPath(LibraryCatalogItemDto item) =>
        item.Mappings.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.LinkPath;

    private static PlexLibraryMedia? Match(LibraryCatalogItemDto item, IPlexLibraryMetadataIndex metadata)
    {
        foreach (var mapping in item.Mappings.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase))
        {
            var match = metadata.Match(mapping.LinkPath);
            if (match is not null) return match;
        }
        return metadata.Match(item.DisplayName);
    }

    private static GroupIdentity Classify(LibraryCatalogItemDto item, PlexLibraryMedia? plex)
    {
        if (plex is null)
        {
            var unmatchedKey = item.DavItemId?.ToString("D") ?? PrimaryLinkPath(item) ?? item.DisplayName;
            return new GroupIdentity("unmatched", $"unmatched/{unmatchedKey}", item.DisplayName);
        }
        var category = plex.MediaType == "episode" ? "shows" : "movies";
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
                    var folderCategory = root switch
                    {
                        "tv" or "shows" or "tv shows" or "series" or "television" => "shows",
                        "movies" or "films" => "movies",
                        _ when root.Length > 3 && root.StartsWith("tv-", StringComparison.Ordinal) => "shows",
                        _ when root.Length > 7 && root.StartsWith("movies-", StringComparison.Ordinal) => "movies",
                        _ => null,
                    };
                    if (folderCategory != category) continue;
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
        var plexTitle = category == "shows" ? plex.ShowName : plex.Title;
        var fallback = string.IsNullOrWhiteSpace(plexTitle) ? item.DisplayName : plexTitle;
        return new GroupIdentity(category, $"{category}/{fallback}", fallback);
    }

    private sealed record GroupIdentity(string Category, string Key, string Title);
    private sealed record ClassifiedItem(LibraryCatalogItemDto Item, GroupIdentity Identity,
        PlexLibraryMedia? PlexMatch);
    private sealed record Group(string Key, string Title, string Category,
        IReadOnlyList<ClassifiedItem> Items);
}
