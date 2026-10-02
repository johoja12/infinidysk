using NzbWebDAV.Database;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Services.Regrab;

/// <summary>Adds the active regrab state to the Media Library items on one browse page.</summary>
public static class LibraryRegrabDecorator
{
    public static async Task<LibraryBrowseResult> DecorateAsync(
        LibraryBrowseResult result,
        ArrRegrabService regrab,
        DavDatabaseContext ctx,
        string? libraryDir,
        CancellationToken ct)
    {
        var items = (result.ExpandedGroup?.Items.Select(x => x.Item) ?? [])
            .Concat(result.Files?.Select(x => x.Item) ?? [])
            .ToArray();
        if (items.Length == 0)
            return result;

        var ids = items.Select(x => x.DavItemId).OfType<Guid>().Distinct().ToArray();
        var paths = items
            .SelectMany(x => x.Mappings)
            .Select(m => ArrRegrabService.AbsoluteLinkPath(m.LinkPath, libraryDir))
            .OfType<string>()
            .Distinct()
            .ToArray();
        var states = await regrab.GetActiveByKeysAsync(ctx, ids, paths, ct).ConfigureAwait(false);
        if (states.Count == 0)
            return result;

        LibraryCatalogItemDto Decorate(LibraryCatalogItemDto item)
        {
            if (item.DavItemId is { } id && states.TryGetValue($"dav:{id:D}", out var byId))
                return item with { RegrabStatus = byId.Status };
            foreach (var mapping in item.Mappings)
            {
                var path = ArrRegrabService.AbsoluteLinkPath(mapping.LinkPath, libraryDir);
                if (path is not null && states.TryGetValue($"link:{path}", out var byPath))
                    return item with { RegrabStatus = byPath.Status };
            }

            return item;
        }

        return result with
        {
            ExpandedGroup = result.ExpandedGroup is null
                ? null
                : result.ExpandedGroup with
                {
                    Items = result.ExpandedGroup.Items.Select(x => x with { Item = Decorate(x.Item) }).ToArray(),
                },
            Files = result.Files?.Select(x => x with { Item = Decorate(x.Item) }).ToArray(),
        };
    }
}
