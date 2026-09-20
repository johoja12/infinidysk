using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Symlinks;
using NzbWebDAV.UsenetMigration.Naming;

namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed class AltmountCorrelationProvider : IMigrationCorrelationProvider
{
    public string SourceType => MigrationSourceTypes.Altmount;

    public Task<IReadOnlyList<MigrationCorrelationResult>> CorrelateAsync(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<DavItem> importedLeaves,
        DavDatabaseContext davContext,
        CancellationToken cancellationToken = default)
    {
        _ = davContext;
        cancellationToken.ThrowIfCancellationRequested();
        var leaves = importedLeaves.Select(item => new ReleaseLeaf
        {
            DavItemId = item.Id,
            Name = item.Name,
            Path = item.Path,
            FileSize = item.FileSize,
            NzbBlobId = item.NzbBlobId,
            IdentityMethod = "release-membership",
        }).ToList();
        var matches = SymlinkMatcher.Match(sourceFiles.Select(file => new MatchableFile
        {
            ReleaseFileId = file.Id,
            NormalisedName = file.NormalisedName,
            NormalisedRelativePath = MatchKey.ForRelativePath(file.VirtualPath),
            FileSize = file.FileSize,
        }).ToList(), leaves).ToDictionary(match => match.ReleaseFileId);
        IReadOnlyList<MigrationCorrelationResult> result = sourceFiles.Select(file =>
        {
            matches.TryGetValue(file.Id, out var match);
            var status = match?.DavItemId is not null ? "exact" : "unmatched-target";
            return new MigrationCorrelationResult(file.Id, status, match?.DavItemId,
                match?.DavItemId is { } id ? importedLeaves.Single(item => item.Id == id).NzbBlobId : null,
                match?.MatchMethod,
                System.Text.Json.JsonSerializer.Serialize(new { method = match?.MatchMethod }));
        }).ToArray();
        return Task.FromResult(result);
    }
}
