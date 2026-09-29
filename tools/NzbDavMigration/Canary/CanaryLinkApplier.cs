using NzbWebDAV.UsenetMigration.Canary;

namespace NzbDavMigration.Canary;

public sealed class CanaryLinkApplier
{
    private readonly Func<string, bool> _isLocalMount;
    private readonly Action<string>? _beforeCreate;
    private readonly Func<NzbDavCanaryPlanLink, string, CancellationToken, Task>? _verifyMapping;
    private readonly Func<NzbDavCanaryPlanLink, string, CancellationToken, Task>? _verifyReplacement;

    public CanaryLinkApplier() : this(CanaryPathSafety.IsLocalMount)
    {
    }

    internal CanaryLinkApplier(
        Func<string, bool> isLocalMount,
        Action<string>? beforeCreate = null,
        Func<NzbDavCanaryPlanLink, string, CancellationToken, Task>? verifyMapping = null,
        Func<NzbDavCanaryPlanLink, string, CancellationToken, Task>? verifyReplacement = null)
    {
        _isLocalMount = isLocalMount;
        _beforeCreate = beforeCreate;
        _verifyMapping = verifyMapping;
        _verifyReplacement = verifyReplacement;
    }

    public async Task<CanaryApplyJournal> ApplyAsync(
        string planPath,
        string sourceRoot,
        string libraryRoot,
        string targetRoot,
        string journalPath,
        IReadOnlySet<string>? missingSourcePaths = null,
        IReadOnlyDictionary<string, string>? replacedSourcePaths = null,
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

        var plannedPaths = verified.Plan.Links.Where(link => link.NewRelativeTarget is not null)
            .Select(link => link.LibraryRelativePath).ToHashSet(StringComparer.Ordinal);
        if (missingSourcePaths is not null && missingSourcePaths.Any(path => !plannedPaths.Contains(path)))
            throw new InvalidDataException("Missing-source exclusions must belong to the exact plan.");
        if (replacedSourcePaths is not null && replacedSourcePaths.Keys.Any(path => !plannedPaths.Contains(path)
            || missingSourcePaths?.Contains(path) == true))
            throw new InvalidDataException("Replaced-source exclusions must belong to the exact plan and be distinct from missing sources.");

        foreach (var planned in verified.Plan.Links.Where(link => link.NewRelativeTarget is not null))
        {
            if (planned.CorrelationStatus != "exact" || planned.ApplyStatus != "planned")
                throw new InvalidDataException($"Actionable link '{planned.LibraryRelativePath}' is not exact.");
            var sourceLinkPath = CanaryPathSafety.ResolveBeneath(
                source, planned.LibraryRelativePath, "source library path");
            var linkPath = CanaryPathSafety.ResolveBeneath(library, planned.LibraryRelativePath, "library path");
            var targetPath = CanaryPathSafety.ResolveBeneath(target, planned.NewRelativeTarget!, "target path");
            var existingJournal = journal.Links.SingleOrDefault(link =>
                link.LibraryRelativePath == planned.LibraryRelativePath);
            if (replacedSourcePaths?.TryGetValue(planned.LibraryRelativePath, out var replacementTarget) == true)
            {
                CanaryPathSafety.EnsureParentsExistWithoutLinks(source, sourceLinkPath);
                CanaryPathSafety.EnsureExistingParentsWithoutLinks(library, linkPath);
                var sourceInfo = new FileInfo(sourceLinkPath);
                sourceInfo.Refresh();
                if (string.IsNullOrWhiteSpace(replacementTarget)
                    || replacementTarget == planned.OriginalLegacyTarget
                    || !replacementTarget.StartsWith(Path.Join(target, ".ids") + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)
                    || sourceInfo.LinkTarget != replacementTarget
                    || CanaryPathSafety.PathExistsNoFollow(linkPath)
                    || existingJournal is not null && (existingJournal.Status != "source-replaced"
                        || existingJournal.ReplacementSourceTarget != replacementTarget
                        || existingJournal.SourceLinkPath != sourceLinkPath
                        || existingJournal.ObservedSourceTarget != planned.OriginalLegacyTarget
                        || existingJournal.LinkPath != linkPath
                        || existingJournal.TargetPath != targetPath
                        || existingJournal.ExpectedFileSize != planned.ExpectedFileSize))
                    throw new InvalidDataException("Replaced source or destination changed since exclusion was recorded.");
                if (_verifyReplacement is not null)
                    await _verifyReplacement(planned, sourceLinkPath, cancellationToken).ConfigureAwait(false);
                if (existingJournal is null)
                {
                    journal.Links.Add(new CanaryApplyJournalLink
                    {
                        LibraryRelativePath = planned.LibraryRelativePath,
                        SourceLinkPath = sourceLinkPath,
                        ObservedSourceTarget = planned.OriginalLegacyTarget,
                        ReplacementSourceTarget = replacementTarget,
                        LinkPath = linkPath,
                        TargetPath = targetPath,
                        ExpectedFileSize = planned.ExpectedFileSize,
                        Status = "source-replaced",
                    });
                    await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (missingSourcePaths?.Contains(planned.LibraryRelativePath) == true)
            {
                CanaryPathSafety.EnsureParentsExistWithoutLinks(source, sourceLinkPath);
                CanaryPathSafety.EnsureExistingParentsWithoutLinks(library, linkPath);
                if (CanaryPathSafety.PathExistsNoFollow(sourceLinkPath)
                    || CanaryPathSafety.PathExistsNoFollow(linkPath)
                    || existingJournal is not null && (existingJournal.Status != "source-missing"
                        || existingJournal.SourceLinkPath != sourceLinkPath
                        || existingJournal.ObservedSourceTarget != planned.OriginalLegacyTarget
                        || existingJournal.LinkPath != linkPath
                        || existingJournal.TargetPath != targetPath
                        || existingJournal.ExpectedFileSize != planned.ExpectedFileSize))
                    throw new InvalidDataException("Excluded source is present or has an existing apply result.");
                if (existingJournal is null)
                {
                    journal.Links.Add(new CanaryApplyJournalLink
                    {
                        LibraryRelativePath = planned.LibraryRelativePath,
                        SourceLinkPath = sourceLinkPath,
                        ObservedSourceTarget = planned.OriginalLegacyTarget,
                        LinkPath = linkPath,
                        TargetPath = targetPath,
                        ExpectedFileSize = planned.ExpectedFileSize,
                        Status = "source-missing",
                    });
                    await CanaryJournalStore.WriteAsync(journalPath, journal, cancellationToken)
                        .ConfigureAwait(false);
                }
                continue;
            }
            if (_verifyMapping is not null)
                await _verifyMapping(planned, sourceLinkPath, cancellationToken).ConfigureAwait(false);
            VerifySourceLink(source, sourceLinkPath, planned.OriginalLegacyTarget);
            VerifyTarget(targetPath, planned.ExpectedFileSize);
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
