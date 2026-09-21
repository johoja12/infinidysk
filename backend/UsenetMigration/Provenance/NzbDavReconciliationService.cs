using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Naming;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed record NzbDavReconciliationResult(
    long RunId,
    int SelectedCount,
    int ExactCount,
    int AmbiguousCount,
    int UnmatchedCount,
    int SubmittedCount);

public sealed class NzbDavReconciliationService(
    UsenetMigrationStore store,
    NzbDavPackageReader packageReader,
    IBlobStore blobStore)
{
    internal Func<DavDatabaseContext>? DavContextFactory { get; set; }

    public async Task<NzbDavReconciliationResult> ReconcileAsync(
        long runId,
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var package = await packageReader.ReadAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        await using var ledger = store.NewContext();
        var session = await ledger.SessionState.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (session.SourceType != MigrationSourceTypes.NzbDav
            || session.Status != MigrationSessionStatus.Complete
            || session.CurrentRunId != runId
            || session.SourcePackageRoot is null
            || !SamePath(session.SourcePackageRoot, package.RootPath))
            throw new InvalidOperationException("NzbDav reconciliation requires the matching terminal session and package.");
        var run = await ledger.MigrationRuns.SingleOrDefaultAsync(item => item.Id == runId, cancellationToken)
            .ConfigureAwait(false);
        if (run is not { SourceType: MigrationSourceTypes.NzbDav, Status: "completed" })
            throw new InvalidOperationException("NzbDav reconciliation requires a completed run.");
        var activeStates = new[] { "pending", "submitting", "submitted", "processing" };
        if (await ledger.Submissions.AnyAsync(
                submission => activeStates.Contains(submission.State), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("NzbDav reconciliation refuses active submissions.");

        var selectedIds = package.Manifest.SelectedLinks
            .Select(link => link.LegacyDavItemId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = await ledger.ReleaseFiles
            .Where(file => file.SourceFileId != null && selectedIds.Contains(file.SourceFileId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (sourceFiles.Count != selectedIds.Count
            || sourceFiles.Select(file => file.SourceFileId!).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != selectedIds.Count)
            throw new InvalidDataException("The migration ledger does not contain every selected package leaf exactly once.");
        if (sourceFiles.Any(file => !HasPackageDigest(file.Flags, package.PackageDigest)))
            throw new InvalidDataException("The immutable package digest no longer matches the migration ledger.");

        var migratedReleases = await ledger.MigratedReleases
            .Where(release => release.SourceType == MigrationSourceTypes.NzbDav
                              && release.LastRunId == runId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var migratedBySource = migratedReleases.ToDictionary(
            release => release.SourceReleaseId, StringComparer.Ordinal);
        var existingFiles = await ledger.MigratedFiles
            .Where(file => migratedReleases.Select(release => release.Id).Contains(file.MigratedReleaseId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var work = new List<ReleaseWork>();
        await using var dav = DavDatabaseContexts.Create(DavContextFactory);
        var provider = new NzbDavCorrelationProvider(new DavItemArticleIdentityReader(blobStore));
        foreach (var exported in package.Manifest.Releases)
        {
            var selectedReleaseIds = exported.Leaves
                .Where(leaf => selectedIds.Contains(leaf.LegacyDavItemId.ToString()))
                .Select(leaf => leaf.LegacyDavItemId.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedReleaseIds.Count == 0)
                continue;
            var storeRef = $"nzbdav:{exported.SourceReleaseId}";
            if (!migratedBySource.TryGetValue(storeRef, out var migrated)
                || migrated.NzoId is null
                || !Guid.TryParse(migrated.NzoId, out var importedNzoId))
                throw new InvalidOperationException($"Run {runId} lacks imported release provenance.");
            var submission = await ledger.Submissions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.StoreRef == storeRef, cancellationToken)
                .ConfigureAwait(false);
            if (submission is null
                || submission.State is not ("completed" or "history_cleared")
                || !string.Equals(submission.NzoId, migrated.NzoId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Imported release '{storeRef}' is not terminal or changed identity.");
            var releaseSources = sourceFiles
                .Where(file => file.StoreRef == storeRef && selectedReleaseIds.Contains(file.SourceFileId!))
                .ToArray();
            if (releaseSources.Length != selectedReleaseIds.Count)
                throw new InvalidDataException($"Release '{storeRef}' is missing selected source leaves.");
            var importedLeaves = await dav.Items.AsNoTracking()
                .Where(item => item.Type == DavItem.ItemType.UsenetFile
                               && (item.NzbBlobId == importedNzoId || item.HistoryItemId == importedNzoId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var scope = new NzbDavCorrelationScope(
                runId,
                storeRef,
                importedNzoId,
                releaseSources.Any(file => file.ArticleIdentityKind == NzbDavArticleIdentity.ArchiveMemberKind));
            var correlations = await provider.CorrelateAsync(
                releaseSources, importedLeaves, dav, scope, cancellationToken).ConfigureAwait(false);
            work.Add(new ReleaseWork(migrated, importedNzoId, releaseSources, correlations));
        }
        if (work.Sum(item => item.Sources.Count) != selectedIds.Count)
            throw new InvalidOperationException("Run provenance does not cover every selected package release.");

        ValidateExactMappings(work, existingFiles);
        await using var transaction = await ledger.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;
        foreach (var item in work)
        {
            foreach (var source in item.Sources)
            {
                var correlation = item.Correlations.Single(result => result.ReleaseFileId == source.Id);
                source.FileStatus = correlation.Status;
                source.NewDavItemId = correlation.DavItemId?.ToString();
                source.Flags = MergeEvidence(package.PackageDigest, correlation.Evidence);
                if (correlation.Status != "exact" || correlation.DavItemId is null)
                    continue;
                var migratedFile = existingFiles.SingleOrDefault(file =>
                    file.MigratedReleaseId == item.Release.Id
                    && file.VirtualPath == source.VirtualPath);
                if (migratedFile is null)
                {
                    migratedFile = new MigratedFile
                    {
                        MigratedReleaseId = item.Release.Id,
                        VirtualPath = source.VirtualPath,
                    };
                    ledger.MigratedFiles.Add(migratedFile);
                    existingFiles.Add(migratedFile);
                }
                migratedFile.NormalisedRelativePath = MatchKey.ForRelativePath(source.VirtualPath);
                migratedFile.NormalisedName = source.NormalisedName;
                migratedFile.FileSize = source.FileSize;
                migratedFile.DavItemId = correlation.DavItemId.Value;
                migratedFile.NzbBlobId = correlation.NzbBlobId ?? item.ImportedNzoId;
                migratedFile.SourceFileId = source.SourceFileId;
                migratedFile.ArticleIdentityKind = source.ArticleIdentityKind;
                migratedFile.ArticleIdentityDigest = source.ArticleIdentityDigest;
                migratedFile.MatchMethod = correlation.MatchMethod ?? "exact";
                migratedFile.LastVerifiedAt = now;
            }
            item.Release.MappedFileCount = item.Correlations.Count(result => result.Status == "exact");
            item.Release.LastVerifiedAt = now;
        }
        await ledger.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var results = work.SelectMany(item => item.Correlations).ToArray();
        return new NzbDavReconciliationResult(
            runId,
            results.Length,
            results.Count(result => result.Status == "exact"),
            results.Count(result => result.Status is "ambiguous" or "duplicate"),
            results.Count(result => result.Status is not ("exact" or "ambiguous" or "duplicate")),
            0);
    }

    private static void ValidateExactMappings(
        IReadOnlyList<ReleaseWork> work,
        IReadOnlyList<MigratedFile> existingFiles)
    {
        foreach (var item in work)
        foreach (var source in item.Sources)
        {
            var result = item.Correlations.Single(value => value.ReleaseFileId == source.Id);
            var persisted = existingFiles.SingleOrDefault(file =>
                file.MigratedReleaseId == item.Release.Id
                && (file.VirtualPath == source.VirtualPath
                    || string.Equals(file.SourceFileId, source.SourceFileId, StringComparison.OrdinalIgnoreCase)));
            Guid? sourceTarget = source.FileStatus == "exact"
                                 && Guid.TryParse(source.NewDavItemId, out var parsed)
                ? parsed
                : null;
            foreach (var existing in new Guid?[] { sourceTarget, persisted?.DavItemId })
            {
                if (existing is null)
                    continue;
                if (result.Status != "exact" || result.DavItemId != existing)
                    throw new InvalidOperationException(
                        $"Reconciliation refuses to replace the existing exact mapping for source file {source.Id}.");
            }
        }
    }

    private static bool HasPackageDigest(string? flags, string digest)
    {
        if (string.IsNullOrWhiteSpace(flags))
            return false;
        try
        {
            using var document = JsonDocument.Parse(flags);
            return document.RootElement.TryGetProperty("packageSha256", out var value)
                   && string.Equals(value.GetString(), digest, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string MergeEvidence(string packageDigest, string evidence)
    {
        using var document = JsonDocument.Parse(evidence);
        return JsonSerializer.Serialize(new
        {
            packageSha256 = packageDigest,
            correlation = document.RootElement.Clone(),
        });
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    private sealed record ReleaseWork(
        MigratedRelease Release,
        Guid ImportedNzoId,
        IReadOnlyList<MigrationReleaseFile> Sources,
        IReadOnlyList<MigrationCorrelationResult> Correlations);
}
