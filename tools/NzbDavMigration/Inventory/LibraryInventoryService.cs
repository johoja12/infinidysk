namespace NzbDavMigration.Inventory;

public sealed record LibraryInventoryLink(
    string LibraryRelativePath,
    string OriginalTarget,
    Guid LegacyDavItemId);

public sealed record LegacyInventoryCandidate(
    string LibraryRelativePath,
    string OriginalTarget,
    Guid LegacyDavItemId,
    Legacy.LegacyDavItemRow? Item,
    string Status,
    string? ExclusionReason,
    string? ResolvedNzbPath);

public sealed class LibraryInventoryService
{
    public IReadOnlyList<LibraryInventoryLink> Inventory(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var root = new DirectoryInfo(Path.GetFullPath(libraryRoot));
        if (!root.Exists)
            throw new DirectoryNotFoundException(root.FullName);
        if (root.LinkTarget is not null)
            throw new InvalidDataException("The library root must not be a symbolic link.");

        var links = new List<LibraryInventoryLink>();
        Walk(root, root.FullName, links);
        return links
            .OrderBy(link => link.LibraryRelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<LegacyInventoryCandidate> Enrich(
        IReadOnlyCollection<LibraryInventoryLink> links,
        IReadOnlyCollection<Legacy.LegacyDavItemRow> rows,
        Legacy.LegacyBlobResolver resolver)
    {
        var indexed = rows.ToDictionary(row => row.Id);
        return links.Select(link => BuildCandidate(
                link, indexed.GetValueOrDefault(link.LegacyDavItemId), resolver))
            .OrderBy(candidate => candidate.LibraryRelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static LegacyInventoryCandidate BuildCandidate(
        LibraryInventoryLink link,
        Legacy.LegacyDavItemRow? item,
        Legacy.LegacyBlobResolver resolver)
    {
        if (item is null)
            return new LegacyInventoryCandidate(link.LibraryRelativePath, link.OriginalTarget,
                link.LegacyDavItemId, null, "excluded", "missing-database-row", null);
        if (item.HistoryDownloadStatus is not null and not 1)
            return new LegacyInventoryCandidate(link.LibraryRelativePath, link.OriginalTarget,
                link.LegacyDavItemId, item, "excluded", "history-not-completed", null);
        if (item.NzbBlobId is null)
            return new LegacyInventoryCandidate(link.LibraryRelativePath, link.OriginalTarget,
                link.LegacyDavItemId, item, "excluded", "missing-nzb-blob-id", null);
        var blobPath = resolver.ResolvePath(item.NzbBlobId.Value);
        if (!File.Exists(blobPath))
            return new LegacyInventoryCandidate(link.LibraryRelativePath, link.OriginalTarget,
                link.LegacyDavItemId, item, "excluded", "missing-nzb-blob", blobPath);
        return new LegacyInventoryCandidate(link.LibraryRelativePath, link.OriginalTarget,
            link.LegacyDavItemId, item, item.HistoryItemId is null ? "candidate-no-history" : "candidate",
            null, blobPath);
    }

    private static void Walk(
        DirectoryInfo directory,
        string root,
        ICollection<LibraryInventoryLink> links)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (entry.LinkTarget is { } target)
            {
                if (TryParseLegacyIdTarget(target, out var id))
                {
                    var relativePath = Path.GetRelativePath(root, entry.FullName)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    links.Add(new LibraryInventoryLink(relativePath, target, id));
                }

                continue;
            }

            if (entry is DirectoryInfo child)
                Walk(child, root, links);
        }
    }

    public static bool TryParseLegacyIdTarget(string target, out Guid id)
    {
        id = Guid.Empty;
        var normalized = target.Replace('\\', '/');
        var marker = normalized.LastIndexOf("/.ids/", StringComparison.Ordinal);
        if (marker < 0)
            return false;
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        return Guid.TryParse(leaf, out id) && id != Guid.Empty;
    }
}
