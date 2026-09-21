using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Source;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.UsenetMigration.Canary;

/// <summary>
/// Builds an immutable host-action plan. This service records intended links but never creates
/// files or symlinks beneath the operator's canary library root.
/// </summary>
public sealed class NzbDavCanaryLinkPlanner
{
    private readonly NzbDavPackageReader _packageReader = new();
    private readonly NzbDavCanaryPlanWriter _writer = new();

    public Task<NzbDavCanaryPlanResult> GenerateAsync(
        UsenetMigrationDbContext context,
        string packageRoot,
        long runId,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(
            context,
            packageRoot,
            runId,
            Path.Join(DavDatabaseContext.ConfigPath, "migration-output", "nzbdav"),
            cancellationToken);

    internal async Task<NzbDavCanaryPlanResult> GenerateAsync(
        UsenetMigrationDbContext context,
        string packageRoot,
        long runId,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var package = await _packageReader.ReadAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var plannedDirectory = NzbDavCanaryPlanWriter.GetPlanDirectory(
            outputRoot, runId, package.PackageDigest);
        if (Directory.Exists(plannedDirectory) || File.Exists(plannedDirectory))
            throw new IOException($"Canary plan '{plannedDirectory}' already exists and will not be overwritten.");
        var duplicate = package.Manifest.SelectedLinks
            .GroupBy(link => link.LibraryRelativePath, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Selected library output path '{duplicate.Key}' is duplicated.");

        var run = await context.MigrationRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == runId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null || run.SourceType != MigrationSourceTypes.NzbDav)
            throw new InvalidOperationException($"Migration run {runId} is not an NzbDav run.");

        var selectedIds = package.Manifest.SelectedLinks
            .Select(link => link.LegacyDavItemId.ToString())
            .ToArray();
        var sourceFiles = await context.ReleaseFiles.AsNoTracking()
            .Where(file => file.SourceFileId != null && selectedIds.Contains(file.SourceFileId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var sourceById = sourceFiles
            .GroupBy(file => file.SourceFileId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var migratedFiles = await (
                from file in context.MigratedFiles.AsNoTracking()
                join release in context.MigratedReleases.AsNoTracking()
                    on file.MigratedReleaseId equals release.Id
                where release.SourceType == MigrationSourceTypes.NzbDav
                      && release.LastRunId == runId
                      && file.SourceFileId != null
                      && selectedIds.Contains(file.SourceFileId)
                select file)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var migratedById = migratedFiles
            .GroupBy(file => file.SourceFileId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var leafById = package.Manifest.Releases.SelectMany(release => release.Leaves)
            .ToDictionary(leaf => leaf.LegacyDavItemId);

        var links = new List<NzbDavCanaryPlanLink>(package.Manifest.SelectedLinks.Count);
        foreach (var selected in package.Manifest.SelectedLinks)
        {
            var id = selected.LegacyDavItemId.ToString();
            sourceById.TryGetValue(id, out var source);
            migratedById.TryGetValue(id, out var migrated);
            var exact = source?.FileStatus == "exact"
                        && migrated is not null
                        && migrated.MatchMethod == "article-identity";
            var status = exact
                ? "exact"
                : source?.FileStatus ?? "not-imported";
            if (status == "exact" && !exact)
                status = "missing-target";
            links.Add(new NzbDavCanaryPlanLink(
                selected.LibraryRelativePath,
                selected.OriginalTarget,
                selected.LegacyDavItemId,
                leafById[selected.LegacyDavItemId].FileSize,
                status,
                source?.Flags ?? "{}",
                exact ? DatabaseStoreSymlinkFile.GetTargetPath(migrated!.DavItemId, '/') : null,
                exact ? "planned" : "not-actionable"));
        }

        var nonExact = links.Where(link =>
            link.CorrelationStatus != "exact"
            || link.NewRelativeTarget is null
            || link.ApplyStatus != "planned").ToArray();
        if (nonExact.Length != 0)
            throw new InvalidDataException(
                $"Every selected link must be exact and actionable; {nonExact.Length} row(s) are not.");

        var actionable = links.Count(link => link.NewRelativeTarget is not null);

        var plan = new NzbDavCanaryPlan(
            NzbDavCanaryPlan.CurrentSchemaVersion,
            runId,
            package.PackageDigest,
            DateTimeOffset.UtcNow,
            links.Count,
            actionable,
            links.Count == package.Manifest.SelectedLinks.Count
            && actionable == links.Count
            && links.All(link => link.CorrelationStatus == "exact"),
            links);

        var now = DateTime.UtcNow;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        context.CanaryLinks.AddRange(links.Select(link => new MigrationCanaryLink
        {
            RunId = runId,
            LibraryRelativePath = link.LibraryRelativePath,
            OriginalLegacyTarget = link.OriginalLegacyTarget,
            NewRelativeTarget = link.NewRelativeTarget,
            CorrelationStatus = link.CorrelationStatus,
            CorrelationEvidence = link.CorrelationEvidence,
            SourcePackageDigest = package.PackageDigest,
            ExpectedFileSize = link.ExpectedFileSize,
            ApplyStatus = link.ApplyStatus,
            CreatedAt = now,
            UpdatedAt = now,
        }));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        string? directory = null;
        try
        {
            directory = await _writer.WriteAsync(outputRoot, plan, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            throw;
        }

        return new NzbDavCanaryPlanResult(plan, directory);
    }
}
