using NzbWebDAV.UsenetMigration.Canary;

namespace NzbDavMigration.Canary;

public sealed class CanaryLinkApplier
{
    private readonly Func<string, bool> _isLocalMount;
    private readonly Action<string>? _beforeCreate;

    public CanaryLinkApplier() : this(CanaryPathSafety.IsLocalMount)
    {
    }

    internal CanaryLinkApplier(Func<string, bool> isLocalMount, Action<string>? beforeCreate = null)
    {
        _isLocalMount = isLocalMount;
        _beforeCreate = beforeCreate;
    }

    public async Task<CanaryApplyJournal> ApplyAsync(
        string planPath,
        string libraryRoot,
        string targetRoot,
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        var library = CanaryPathSafety.ResolveRoot(libraryRoot, "Canary library root");
        var target = CanaryPathSafety.ResolveRoot(targetRoot, "InfiniDysk target root");
        if (!_isLocalMount(library))
            throw new InvalidDataException("Canary library root must be on a local filesystem, not NFS or FUSE.");
        var verified = await CanaryPlanVerifier.ReadAsync(planPath, cancellationToken).ConfigureAwait(false);
        var fullPlanPath = Path.GetFullPath(planPath);
        var journal = await CanaryJournalStore.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false)
                      ?? new CanaryApplyJournal
                      {
                          PlanPath = fullPlanPath,
                          PlanSha256 = verified.PlanSha256,
                          LibraryRoot = library,
                          TargetRoot = target,
                      };
        if (journal.PlanPath != fullPlanPath
            || journal.PlanSha256 != verified.PlanSha256
            || journal.LibraryRoot != library
            || journal.TargetRoot != target)
            throw new InvalidDataException("Existing journal belongs to a different plan or root set.");
        await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

        foreach (var planned in verified.Plan.Links.Where(link => link.NewRelativeTarget is not null))
        {
            if (planned.CorrelationStatus != "exact" || planned.ApplyStatus != "planned")
                throw new InvalidDataException($"Actionable link '{planned.LibraryRelativePath}' is not exact.");
            var linkPath = CanaryPathSafety.ResolveBeneath(library, planned.LibraryRelativePath, "library path");
            var targetPath = CanaryPathSafety.ResolveBeneath(target, planned.NewRelativeTarget!, "target path");
            VerifyTarget(targetPath, planned.ExpectedFileSize);
            var existingJournal = journal.Links.SingleOrDefault(link =>
                link.LibraryRelativePath == planned.LibraryRelativePath);
            if (CanaryPathSafety.PathExistsNoFollow(linkPath))
            {
                var info = new FileInfo(linkPath);
                if (existingJournal?.Status == "applied"
                    && info.LinkTarget == targetPath
                    && existingJournal.LinkPath == linkPath
                    && existingJournal.TargetPath == targetPath)
                    continue;
                throw new IOException($"Refusing to overwrite existing unowned path '{linkPath}'.");
            }

            foreach (var directory in CanaryPathSafety.EnsureParentDirectories(library, linkPath))
            {
                if (!journal.CreatedDirectories.Contains(directory, StringComparer.Ordinal))
                    journal.CreatedDirectories.Add(directory);
            }
            var journalLink = existingJournal ?? new CanaryApplyJournalLink
            {
                LibraryRelativePath = planned.LibraryRelativePath,
                LinkPath = linkPath,
                TargetPath = targetPath,
                ExpectedFileSize = planned.ExpectedFileSize,
            };
            if (existingJournal is null)
                journal.Links.Add(journalLink);
            journalLink.Status = "pending";
            journalLink.Error = null;
            await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

            _beforeCreate?.Invoke(targetPath);
            var immediate = await CanaryPlanVerifier.ReadAsync(planPath, cancellationToken).ConfigureAwait(false);
            if (immediate.PlanSha256 != verified.PlanSha256
                || !immediate.Plan.Links.Contains(planned))
                throw new InvalidDataException("Canary plan changed during apply.");
            if (CanaryPathSafety.ResolveRoot(library, "Canary library root") != library
                || CanaryPathSafety.ResolveRoot(target, "InfiniDysk target root") != target)
                throw new InvalidDataException("Canary roots changed during apply.");
            _ = CanaryPathSafety.ResolveBeneath(library, planned.LibraryRelativePath, "library path");
            _ = CanaryPathSafety.ResolveBeneath(target, planned.NewRelativeTarget!, "target path");
            CanaryPathSafety.EnsureParentsExistWithoutLinks(library, linkPath);
            VerifyTarget(targetPath, planned.ExpectedFileSize);
            if (CanaryPathSafety.PathExistsNoFollow(linkPath))
                throw new IOException($"Canary output appeared before link creation: '{linkPath}'.");
            File.CreateSymbolicLink(linkPath, targetPath);
            journalLink.Status = "applied";
            await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        }

        return journal;
    }

    private static void VerifyTarget(string targetPath, long expectedSize)
    {
        var info = new FileInfo(targetPath);
        if (!info.Exists || info.LinkTarget is not null)
            throw new FileNotFoundException("Planned InfiniDysk target is missing or not a regular file.", targetPath);
        if (info.Length != expectedSize)
            throw new InvalidDataException(
                $"Planned target '{targetPath}' has size {info.Length}, expected {expectedSize}.");
    }
}
