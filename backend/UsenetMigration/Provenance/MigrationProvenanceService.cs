using Microsoft.EntityFrameworkCore;
using NzbWebDAV.UsenetMigration.Naming;
using NzbWebDAV.UsenetMigration.Symlinks;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Provenance;

/// <summary>
/// Persists successful source-to-NzbDAV mappings independently from the current
/// wizard scan so later migration runs can still rewrite earlier symlinks.
/// </summary>
public sealed class MigrationProvenanceService
{
    private readonly MigrationCorrelationDispatcher _correlationDispatcher;

    public MigrationProvenanceService(MigrationCorrelationDispatcher? correlationDispatcher = null)
    {
        _correlationDispatcher = correlationDispatcher ?? CreateDefaultDispatcher();
    }

    public async Task<int> RecordCompletedAsync(
        UsenetMigrationDbContext migrationContext,
        DavDatabaseContext davContext,
        MigrationSubmission submission,
        Guid nzoId,
        HistoryItem history,
        CancellationToken ct = default)
    {
        var session = await UsenetMigrationStore.GetOrCreateSessionAsync(migrationContext, ct).ConfigureAwait(false);
        var sourceType = session.SourceType;
        var runId = await EnsureCurrentRunAsync(migrationContext, sourceType, ct).ConfigureAwait(false);
        var release = await migrationContext.Releases.AsNoTracking()
            .FirstAsync(r => r.StoreRef == submission.StoreRef, ct)
            .ConfigureAwait(false);
        var sourceFiles = await migrationContext.ReleaseFiles
            .Where(f => f.StoreRef == submission.StoreRef)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var leaves = await davContext.Items.AsNoTracking()
            .Where(i => (i.NzbBlobId == nzoId || i.HistoryItemId == nzoId)
                        && i.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var correlations = await _correlationDispatcher.Resolve(sourceType)
            .CorrelateAsync(sourceFiles, leaves, davContext, ct).ConfigureAwait(false);
        var exactByFileId = correlations.Where(result => result.Status == "exact" && result.DavItemId is not null)
            .ToDictionary(result => result.ReleaseFileId);
        if (sourceType == MigrationSourceTypes.NzbDav)
        {
            foreach (var sourceFile in sourceFiles)
            {
                var result = correlations.Single(item => item.ReleaseFileId == sourceFile.Id);
                sourceFile.FileStatus = result.Status;
                sourceFile.Flags = result.Evidence;
                sourceFile.NewDavItemId = result.DavItemId?.ToString();
            }
        }

        var now = DateTime.UtcNow;
        var migratedRelease = await migrationContext.MigratedReleases
            .FirstOrDefaultAsync(
                r => r.SourceType == sourceType && r.SourceReleaseId == submission.StoreRef,
                ct)
            .ConfigureAwait(false);
        if (migratedRelease is null)
        {
            migratedRelease = new MigratedRelease
            {
                SourceType = sourceType,
                SourceReleaseId = submission.StoreRef,
                FirstRunId = runId,
                MigratedAt = now,
            };
            migrationContext.MigratedReleases.Add(migratedRelease);
            await migrationContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        migratedRelease.LastRunId = runId;
        migratedRelease.NzoId = nzoId.ToString();
        migratedRelease.TargetCategory = history.Category;
        migratedRelease.JobName = history.JobName;
        migratedRelease.MountPath = $"/content/{history.Category}/{history.JobName}";
        migratedRelease.ExpectedFileCount = sourceFiles.Count;
        migratedRelease.MappedFileCount = exactByFileId.Count;
        migratedRelease.LastVerifiedAt = now;

        var existingFiles = await migrationContext.MigratedFiles
            .Where(f => f.MigratedReleaseId == migratedRelease.Id)
            .ToDictionaryAsync(f => f.VirtualPath, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        foreach (var sourceFile in sourceFiles)
        {
            if (!exactByFileId.TryGetValue(sourceFile.Id, out var match))
                continue;

            if (!existingFiles.TryGetValue(sourceFile.VirtualPath, out var migratedFile))
            {
                migratedFile = new MigratedFile
                {
                    MigratedReleaseId = migratedRelease.Id,
                    VirtualPath = sourceFile.VirtualPath,
                };
                migrationContext.MigratedFiles.Add(migratedFile);
            }

            migratedFile.NormalisedRelativePath = MatchKey.ForRelativePath(sourceFile.VirtualPath);
            migratedFile.NormalisedName = sourceFile.NormalisedName;
            migratedFile.FileSize = sourceFile.FileSize;
            migratedFile.DavItemId = match.DavItemId!.Value;
            migratedFile.NzbBlobId = match.NzbBlobId ?? nzoId;
            migratedFile.SourceFileId = sourceFile.SourceFileId;
            migratedFile.ArticleIdentityKind = sourceFile.ArticleIdentityKind;
            migratedFile.ArticleIdentityDigest = sourceFile.ArticleIdentityDigest;
            migratedFile.MatchMethod = match.MatchMethod ?? "exact";
            migratedFile.LastVerifiedAt = now;
        }

        if (exactByFileId.Count == sourceFiles.Count)
        {
            var currentPaths = sourceFiles.Select(f => f.VirtualPath).ToHashSet(StringComparer.Ordinal);
            migrationContext.MigratedFiles.RemoveRange(
                existingFiles.Values.Where(f => !currentPaths.Contains(f.VirtualPath)));
        }

        Log.Information(
            "Recorded migration provenance for {StoreRef}: Run={RunId}, ExpectedFiles={Expected}, " +
            "MappedFiles={Mapped}, NzoId={NzoId}",
            submission.StoreRef, runId, sourceFiles.Count, exactByFileId.Count, nzoId);
        return exactByFileId.Count;
    }

    private static async Task<long> EnsureCurrentRunAsync(
        UsenetMigrationDbContext context,
        string sourceType,
        CancellationToken ct)
    {
        var session = await UsenetMigrationStore.GetOrCreateSessionAsync(context, ct).ConfigureAwait(false);
        if (session.CurrentRunId is { } currentRunId
            && await context.MigrationRuns.AnyAsync(r => r.Id == currentRunId, ct).ConfigureAwait(false))
            return currentRunId;

        var now = DateTime.UtcNow;
        var run = new MigrationRun
        {
            SourceType = sourceType,
            Status = "running",
            StartedAt = now,
        };
        context.MigrationRuns.Add(run);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        session.CurrentRunId = run.Id;
        session.RunStartedAt ??= now;
        session.UpdatedAt = now;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return run.Id;
    }

    private static MigrationCorrelationDispatcher CreateDefaultDispatcher()
    {
        IMigrationCorrelationProvider[] providers =
        [
            new AltmountCorrelationProvider(),
            new NzbDavCorrelationProvider(new DavItemArticleIdentityReader(BlobStore.Current)),
        ];
        return new MigrationCorrelationDispatcher(providers);
    }
}
