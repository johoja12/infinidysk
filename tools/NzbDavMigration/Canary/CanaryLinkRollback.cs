namespace NzbDavMigration.Canary;

public sealed class CanaryLinkRollback
{
    public async Task<CanaryRollbackResult> RollbackAsync(
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        var journal = await CanaryJournalStore.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false)
                      ?? throw new FileNotFoundException("Canary apply journal is missing.", journalPath);
        var libraryRoot = CanaryPathSafety.ResolveRoot(journal.LibraryRoot, "Canary library root");
        var targetRoot = CanaryPathSafety.ResolveRoot(journal.TargetRoot, "InfiniDysk target root");
        var removed = 0;
        var skipped = 0;
        foreach (var link in journal.Links.AsEnumerable().Reverse())
        {
            var expectedLinkPath = CanaryPathSafety.ResolveBeneath(
                libraryRoot, link.LibraryRelativePath, "journaled library path");
            if (expectedLinkPath != Path.GetFullPath(link.LinkPath)
                || !IsBeneath(targetRoot, link.TargetPath))
                throw new InvalidDataException("Apply journal contains a path outside its recorded roots.");
            var info = new FileInfo(link.LinkPath);
            var exists = CanaryPathSafety.PathExistsNoFollow(link.LinkPath);
            if (exists)
                CanaryPathSafety.EnsureParentsExistWithoutLinks(libraryRoot, link.LinkPath);
            if (exists && info.LinkTarget == link.TargetPath)
            {
                File.Delete(link.LinkPath);
                link.Status = "rolled-back";
                link.Error = null;
                removed++;
            }
            else
            {
                link.Status = "rollback-skipped";
                link.Error = "Link is missing or no longer has the journaled target.";
                skipped++;
            }
            await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        }

        var removedDirectories = 0;
        foreach (var directory in journal.CreatedDirectories
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(path => path.Length))
        {
            if (!IsBeneath(libraryRoot, directory))
                throw new InvalidDataException("Apply journal contains a created directory outside its library root.");
            var info = new DirectoryInfo(directory);
            if (!info.Exists || info.LinkTarget is not null)
                continue;
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                continue;
            Directory.Delete(directory);
            removedDirectories++;
        }
        await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        return new CanaryRollbackResult(removed, skipped, removedDirectories);
    }

    private static bool IsBeneath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
