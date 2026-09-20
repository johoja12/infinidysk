using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.UsenetMigration.Naming;
using NzbWebDAV.UsenetMigration.Provenance;
using NzbWebDAV.UsenetMigration.Source;
using NzbWebDAV.Utils;

namespace NzbWebDAV.UsenetMigration.Runner;

public sealed class NzbDavScanRunner(
    UsenetMigrationStore store,
    ConfigManager configManager,
    NzbDavPackageReader packageReader) : IUsenetMigrationScanRunner
{
    public string SourceType => MigrationSourceTypes.NzbDav;

    public async Task<ScanSummary?> ScanAsync(CancellationToken cancellationToken = default)
    {
        var session = await store.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var packageRoot = session.SourcePackageRoot
                          ?? throw new InvalidOperationException("NzbDav scan requires SourcePackageRoot.");
        var package = await packageReader.ReadAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var categoryMap = (await store.GetCategoryMapAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.AltmountCategory, StringComparer.Ordinal);
        await RejectArrBoundTargetsAsync(categoryMap.Values, cancellationToken).ConfigureAwait(false);

        if (!await store.CaptureScanStartAsync(
                configManager.IsLazyRarParsingEnabled(),
                PathSanitizer.IsWindowsSafePathsEnabled,
                cancellationToken).ConfigureAwait(false))
            return null;

        var selectedById = package.Manifest.SelectedLinks.ToDictionary(link => link.LegacyDavItemId);
        var releases = new List<(MigrationRelease Release, MigrationReleaseFile[] Files, string[] Errors)>();
        foreach (var source in package.Manifest.Releases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new List<string>();
            var categories = source.Leaves
                .Where(leaf => selectedById.ContainsKey(leaf.LegacyDavItemId))
                .Select(leaf => TopLevel(selectedById[leaf.LegacyDavItemId].LibraryRelativePath))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (categories.Length != 1)
                errors.Add("inconsistent_source_category");
            var sourceCategory = categories.Length == 1 ? categories[0] : "";
            categoryMap.TryGetValue(sourceCategory, out var mapping);
            if (mapping is not { Action: "migrate", TargetCategory.Length: > 0 })
                errors.Add("category_unmapped");
            foreach (var leaf in source.Leaves.Where(leaf => leaf.ExtractionStatus != "ready"))
                errors.Add($"source_exclusion:{leaf.ExclusionReason ?? "unknown"}");

            var payload = package.Manifest.Payloads.Single(item => item.RelativePath == source.PayloadPath);
            var document = await LoadNzbSecurelyAsync(
                package.PayloadPaths[source.PayloadPath], cancellationToken).ConfigureAwait(false);
            var submitName = Path.GetFileName(source.PayloadPath);
            var queueName = NzbDavNaming.QueueFileName(submitName);
            var jobName = NzbDavNaming.JobName(submitName);
            var storeRef = $"nzbdav:{source.SourceReleaseId}";
            var release = new MigrationRelease
            {
                StoreRef = storeRef,
                StoreBasename = source.SourceReleaseId,
                SubmitFileName = submitName,
                QueueFileName = queueName,
                JobName = jobName,
                JobNameDiverges = !string.Equals(jobName, submitName, StringComparison.Ordinal),
                AltmountCategory = sourceCategory,
                TargetCategory = mapping?.TargetCategory,
                Verdict = errors.Count == 0 ? "green" : "red",
                VerdictReasons = JsonSerializer.Serialize(errors),
                MetaFileCount = source.Leaves.Count,
                TotalBytes = source.Leaves.Sum(leaf => leaf.FileSize),
                NzbFileCount = document.Files.Count,
                SegmentCount = document.Files.Sum(file => file.Segments.Count),
                Included = mapping is { Action: "migrate" },
                ScannedAt = DateTime.UtcNow,
            };
            var files = source.Leaves.Select(leaf => new MigrationReleaseFile
            {
                StoreRef = storeRef,
                MetaPath = source.PayloadPath,
                VirtualPath = leaf.LegacyPath,
                FileName = Path.GetFileName(leaf.LegacyPath),
                NormalisedName = MatchKey.ForLeaf(Path.GetFileName(leaf.LegacyPath)),
                FileSize = leaf.FileSize,
                FileStatus = leaf.ExtractionStatus,
                SourceFileId = leaf.LegacyDavItemId.ToString(),
                ArticleIdentityKind = leaf.IdentityKind,
                ArticleIdentityDigest = leaf.IdentityDigest,
                Flags = JsonSerializer.Serialize(new { packageSha256 = package.PackageDigest, payload.Length }),
            }).ToArray();
            releases.Add((release, files, errors.ToArray()));
        }

        return await PersistAsync(releases, cancellationToken).ConfigureAwait(false);
    }

    private async Task RejectArrBoundTargetsAsync(
        IEnumerable<MigrationCategoryMap> mappings,
        CancellationToken cancellationToken)
    {
        var targets = mappings
            .Where(mapping => mapping.Action == "migrate" && !string.IsNullOrWhiteSpace(mapping.TargetCategory))
            .Select(mapping => mapping.TargetCategory!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
            return;
        foreach (var client in configManager.GetArrConfig().GetArrClients())
        {
            var downloadClients = await client.GetDownloadClientsAsync(cancellationToken).ConfigureAwait(false);
            var conflict = downloadClients.Select(item => item.Category)
                .FirstOrDefault(category => category is not null && targets.Contains(category));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"Migration target category '{conflict}' is bound to an enabled Arr instance.");
        }
    }

    private async Task<ScanSummary?> PersistAsync(
        List<(MigrationRelease Release, MigrationReleaseFile[] Files, string[] Errors)> releases,
        CancellationToken cancellationToken)
    {
        await using var db = store.NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var claimed = await db.SessionState
            .Where(session => session.Id == UsenetMigrationStore.SessionId
                              && session.Status == MigrationSessionStatus.Scanning
                              && session.SourceType == MigrationSourceTypes.NzbDav)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, MigrationSessionStatus.Scanned)
                .SetProperty(session => session.ScanCompletedAt, now)
                .SetProperty(session => session.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await UsenetMigrationStore.ClearScanArtifactsAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var item in releases)
        {
            db.Releases.Add(item.Release);
            db.ReleaseFiles.AddRange(item.Files);
            db.ScanErrors.AddRange(item.Errors.Select(error => new MigrationScanError
            {
                Path = item.Release.StoreRef,
                Kind = "nzbdav_package",
                Message = error,
                OccurredAt = now,
            }));
            if (item.Release is { Included: true, Verdict: "green" })
                db.Submissions.Add(new MigrationSubmission
                {
                    StoreRef = item.Release.StoreRef,
                    State = "pending",
                    UpdatedAt = now,
                });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ScanSummary
        {
            MetaCount = releases.Sum(item => item.Files.Length),
            ReleaseCount = releases.Count,
            GreenCount = releases.Count(item => item.Release.Verdict == "green"),
            RedCount = releases.Count(item => item.Release.Verdict == "red"),
            ScanErrorCount = releases.Sum(item => item.Errors.Length),
        };
    }

    private static async Task<NzbDocument> LoadNzbSecurelyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        await NzbXmlSecurity.ValidateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        stream.Position = 0;
        return await NzbDocument.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static string TopLevel(string relativePath)
    {
        var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? "" : relativePath[..slash];
    }
}
