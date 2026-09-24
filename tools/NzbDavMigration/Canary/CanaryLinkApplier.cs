using NzbWebDAV.UsenetMigration.Canary;

namespace NzbDavMigration.Canary;

public sealed class CanaryLinkApplier
{
    private readonly Func<string, bool> _isLocalMount;
    private readonly Action<string>? _beforeCreate;
    private readonly Func<NzbDavCanaryPlanLink, string, CancellationToken, Task>? _verifyMapping;

    public CanaryLinkApplier() : this(CanaryPathSafety.IsLocalMount)
    {
    }

    internal CanaryLinkApplier(
        Func<string, bool> isLocalMount,
        Action<string>? beforeCreate = null,
        Func<NzbDavCanaryPlanLink, string, CancellationToken, Task>? verifyMapping = null)
    {
        _isLocalMount = isLocalMount;
        _beforeCreate = beforeCreate;
        _verifyMapping = verifyMapping;
    }

    public async Task<CanaryApplyJournal> ApplyAsync(
        string planPath,
        string sourceRoot,
        string libraryRoot,
        string targetRoot,
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        var source = CanaryPathSafety.ResolveRoot(sourceRoot, "Source library root");
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
                          SourceRoot = source,
                          LibraryRoot = library,
                          TargetRoot = target,
                      };
        if (journal.PlanPath != fullPlanPath
            || journal.PlanSha256 != verified.PlanSha256
            || journal.SourceRoot != source
            || journal.LibraryRoot != library
            || journal.TargetRoot != target)
            throw new InvalidDataException("Existing journal belongs to a different plan or root set.");
        await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

        foreach (var planned in verified.Plan.Links.Where(link => link.NewRelativeTarget is not null))
        {
            if (planned.CorrelationStatus != "exact" || planned.ApplyStatus != "planned")
                throw new InvalidDataException($"Actionable link '{planned.LibraryRelativePath}' is not exact.");
            var sourceLinkPath = CanaryPathSafety.ResolveBeneath(
                source, planned.LibraryRelativePath, "source library path");
            var linkPath = CanaryPathSafety.ResolveBeneath(library, planned.LibraryRelativePath, "library path");
            var targetPath = CanaryPathSafety.ResolveBeneath(target, planned.NewRelativeTarget!, "target path");
            if (_verifyMapping is not null)
                await _verifyMapping(planned, sourceLinkPath, cancellationToken).ConfigureAwait(false);
            VerifySourceLink(source, sourceLinkPath, planned.OriginalLegacyTarget);
            VerifyTarget(targetPath, planned.ExpectedFileSize);
            var existingJournal = journal.Links.SingleOrDefault(link =>
                link.LibraryRelativePath == planned.LibraryRelativePath);
            if (CanaryPathSafety.PathExistsNoFollow(linkPath))
            {
                var info = new FileInfo(linkPath);
                if (existingJournal?.Status == "applied"
                    && info.LinkTarget == targetPath
                    && existingJournal.SourceLinkPath == sourceLinkPath
                    && existingJournal.ObservedSourceTarget == planned.OriginalLegacyTarget
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
                SourceLinkPath = sourceLinkPath,
                ObservedSourceTarget = planned.OriginalLegacyTarget,
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
                || CanaryPathSafety.ResolveRoot(source, "Source library root") != source
                || CanaryPathSafety.ResolveRoot(target, "InfiniDysk target root") != target)
                throw new InvalidDataException("Canary roots changed during apply.");
            _ = CanaryPathSafety.ResolveBeneath(source, planned.LibraryRelativePath, "source library path");
            _ = CanaryPathSafety.ResolveBeneath(library, planned.LibraryRelativePath, "library path");
            _ = CanaryPathSafety.ResolveBeneath(target, planned.NewRelativeTarget!, "target path");
            if (_verifyMapping is not null)
                await _verifyMapping(planned, sourceLinkPath, cancellationToken).ConfigureAwait(false);
            VerifySourceLink(source, sourceLinkPath, planned.OriginalLegacyTarget);
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

    private static void VerifySourceLink(string sourceRoot, string sourceLinkPath, string expectedTarget)
    {
        CanaryPathSafety.EnsureParentsExistWithoutLinks(sourceRoot, sourceLinkPath);
        if (!CanaryPathSafety.PathExistsNoFollow(sourceLinkPath))
            throw new FileNotFoundException("Planned source link is missing.", sourceLinkPath);
        var info = new FileInfo(sourceLinkPath);
        info.Refresh();
        if (info.LinkTarget is null || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Planned source path is no longer a symbolic link: '{sourceLinkPath}'.");
        if (!string.Equals(info.LinkTarget, expectedTarget, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Planned source link changed: '{sourceLinkPath}' now targets '{info.LinkTarget}'.");
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
