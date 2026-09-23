using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Read-only catalog: one row per <c>/content</c> Usenet file (deduplicated by
/// <c>DavItem.Id</c>, size = the DavItem file size) plus one row per external
/// symlink link path. Search spans item name/path and mapping link/target.
/// </summary>
public sealed class LibraryCatalogService(DavDatabaseContext context)
{
    private static readonly Regex EpisodePattern = new(
        @"\bS(?<season>\d{1,2})[ ._-]*E(?<episode>\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<LibraryBrowseResult> BrowseAsync(
        LibraryCatalogQuery query,
        LibraryCatalogScanner? scanner = null,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var items = await context.Items.AsNoTracking()
            .Where(i => i.Type == DavItem.ItemType.UsenetFile && i.Path.StartsWith("/content/"))
            .ToListAsync(ct).ConfigureAwait(false);
        var maps = await context.LinkMaps.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var byItem = maps.Where(m => m.DavItemId.HasValue)
            .GroupBy(m => m.DavItemId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryLinkMap>)g.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase).ToList());

        var rows = new List<(LibraryCatalogItemDto Item, string Kind, string Name, string? Episode)>();
        foreach (var item in items)
        {
            byItem.TryGetValue(item.Id, out var itemMaps);
            itemMaps ??= [];
            var dto = ToInternalDto(item, itemMaps);
            if (!PassesTypeFilter(dto, query.TypeFilter)) continue;
            var classifications = itemMaps.Select(m => Classify(m.LinkPath))
                .Where(c => c.Kind != "unmatched")
                .DistinctBy(c => $"{c.Kind}:{c.Name}", StringComparer.OrdinalIgnoreCase)
                .ToList();
            var classification = classifications.Count == 1
                ? classifications[0] : (Kind: "unmatched", Name: "Unmatched");
            rows.Add((dto, classification.Kind ?? "unmatched", classification.Name ?? "Unmatched",
                classification.Kind == "show"
                    ? itemMaps.Select(m => EpisodeLabel(m.LinkPath)).FirstOrDefault(label => label != null)
                    : null));
        }
        foreach (var external in maps.Where(m => m.DavItemId == null)
                     .GroupBy(m => m.LinkPath, StringComparer.Ordinal))
        {
            var dto = ToExternalDto(external.ToList());
            if (PassesTypeFilter(dto, query.TypeFilter))
                rows.Add((dto, "unmatched", "Unmatched", null));
        }

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            rows = rows.Where(row => row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (row.Item.ContentPath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || row.Item.Mappings.Any(m => m.LinkPath.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || m.TargetText.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var groups = rows.GroupBy(row => (row.Kind, Name: row.Name.ToUpperInvariant()))
            .Select(g => new LibraryBrowseGroup(
                $"{g.Key.Kind}:{g.Key.Name}", g.Key.Kind, g.First().Name,
                g.OrderBy(row => row.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new LibraryBrowseFile(row.Item, row.Episode)).ToList(),
                g.Count(), g.Sum(row => row.Item.MappingCount),
                g.Sum(row => row.Item.Size ?? 0),
                g.Count(row => row.Item.Health == "attention")))
            .ToList();
        var showCount = groups.Count(g => g.Kind == "show");
        var movieCount = groups.Count(g => g.Kind == "movie");
        var unmatchedCount = groups.Count(g => g.Kind == "unmatched");
        groups = groups.OrderBy(g => g.Kind == "show" ? 0 : g.Kind == "movie" ? 1 : 2)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new LibraryBrowseResult(
            groups.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            groups.Count, rows.Count, showCount, movieCount, unmatchedCount,
            page, pageSize, scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning);
    }

    private static (string? Kind, string? Name) Classify(string linkPath)
    {
        var parts = linkPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 2 < parts.Length; i++)
        {
            var marker = parts[i].Trim().ToLowerInvariant();
            var kind = marker switch
            {
                "tv" or "tv shows" or "shows" or "series" => "show",
                "movies" or "films" => "movie",
                _ => null,
            };
            if (kind != null && !string.IsNullOrWhiteSpace(parts[i + 1]))
                return (kind, parts[i + 1].Trim());
        }
        return ("unmatched", "Unmatched");
    }

    private static string? EpisodeLabel(string linkPath)
    {
        var match = EpisodePattern.Match(Path.GetFileName(linkPath));
        return match.Success
            ? $"S{int.Parse(match.Groups["season"].Value):00}E{int.Parse(match.Groups["episode"].Value):00}"
            : null;
    }

    public async Task<LibraryCatalogResult> QueryAsync(
        LibraryCatalogQuery query,
        LibraryCatalogScanner? scanner = null,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var search = query.Search?.Trim();
        var descending = string.Equals(query.Direction, "desc", StringComparison.OrdinalIgnoreCase);

        var maps = context.LinkMaps.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            maps = maps.Where(m =>
                m.LinkPath.Contains(search) || m.TargetText.Contains(search));
        }

        var items = context.Items.AsNoTracking()
            .Where(i => i.Type == DavItem.ItemType.UsenetFile
                && i.Path.StartsWith("/content/"));
        if (!string.IsNullOrEmpty(search))
        {
            var matchingIds = maps
                .Where(m => m.DavItemId != null)
                .Select(m => m.DavItemId!.Value)
                .Distinct();
            items = items.Where(i =>
                i.Name.Contains(search) || i.Path.Contains(search) || matchingIds.Contains(i.Id));
        }

        var internalRows = await items
            .Select(i => new { Item = i })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var internalIds = internalRows.Select(r => r.Item.Id).ToList();
        var mappingsByItem = (await maps
                .Where(m => m.DavItemId != null && internalIds.Contains(m.DavItemId.Value))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(m => m.DavItemId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryLinkMap>)g.ToList());

        var dtos = new List<LibraryCatalogItemDto>();
        foreach (var row in internalRows)
        {
            mappingsByItem.TryGetValue(row.Item.Id, out var mappings);
            mappings ??= [];
            var dto = ToInternalDto(row.Item, mappings);
            if (PassesTypeFilter(dto, query.TypeFilter))
                dtos.Add(dto);
        }

        var externalMaps = await maps
            .Where(m => m.DavItemId == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var group in externalMaps.GroupBy(m => m.LinkPath, StringComparer.Ordinal))
        {
            var dto = ToExternalDto(group.ToList());
            if (PassesTypeFilter(dto, query.TypeFilter))
                dtos.Add(dto);
        }

        dtos = SortDtos(dtos, query.Sort, descending);
        var total = dtos.Count;
        var pageItems = dtos.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new LibraryCatalogResult(
            pageItems, total, page, pageSize,
            scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning);
    }

    private static LibraryCatalogItemDto ToInternalDto(DavItem item, IReadOnlyList<LibraryLinkMap> mappings) =>
        new("internal", item.Id, item.Name, item.Path, item.FileSize, mappings.Count,
            mappings.Count == 0 ? "unmapped"
                : mappings.All(m => m.Status == LibraryLinkStatus.Valid) ? "healthy" : "attention",
            mappings.Select(m => new LibraryCatalogMappingDto(
                m.LinkPath, m.TargetText, m.MappingType.ToString().ToLowerInvariant(),
                m.Status.ToString().ToLowerInvariant())).ToList());

    private static LibraryCatalogItemDto ToExternalDto(List<LibraryLinkMap> group)
    {
        var first = group[0];
        return new("external", null, first.LinkPath, null, null, group.Count,
            group.All(m => m.Status == LibraryLinkStatus.Valid) ? "external" : "attention",
            group.Select(m => new LibraryCatalogMappingDto(
                m.LinkPath, m.TargetText, "external",
                m.Status.ToString().ToLowerInvariant())).ToList());
    }

    private static bool PassesTypeFilter(LibraryCatalogItemDto dto, string filter) =>
        filter switch
        {
            "internal" => dto.Kind == "internal",
            "external" => dto.Kind == "external",
            "broken" => dto.Mappings.Any(m => m.Status is "broken" or "stale"),
            _ => true,
        };

    private static List<LibraryCatalogItemDto> SortDtos(
        List<LibraryCatalogItemDto> dtos, string sort, bool descending)
    {
        IOrderedEnumerable<LibraryCatalogItemDto> ordered = sort switch
        {
            "size" => descending
                ? dtos.OrderByDescending(d => d.Size ?? -1).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.Size ?? -1).ThenBy(d => d.DisplayName, StringComparer.Ordinal),
            "mappings" => descending
                ? dtos.OrderByDescending(d => d.MappingCount).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.MappingCount).ThenBy(d => d.DisplayName, StringComparer.Ordinal),
            _ => descending
                ? dtos.OrderByDescending(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.DisplayName, StringComparer.Ordinal),
        };
        return ordered.ToList();
    }
}
