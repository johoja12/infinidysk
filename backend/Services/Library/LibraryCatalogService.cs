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
