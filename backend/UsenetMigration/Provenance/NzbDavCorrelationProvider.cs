using System.Text.Json;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Naming;

namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed class NzbDavCorrelationProvider(DavItemArticleIdentityReader identityReader)
    : IMigrationCorrelationProvider
{
    public string SourceType => MigrationSourceTypes.NzbDav;

    public async Task<IReadOnlyList<MigrationCorrelationResult>> CorrelateAsync(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<DavItem> importedLeaves,
        DavDatabaseContext davContext,
        CancellationToken cancellationToken = default)
    {
        _ = davContext;
        var identities = new List<ImportedArticleIdentity>(importedLeaves.Count);
        foreach (var leaf in importedLeaves)
            identities.Add(await identityReader.ReadAsync(leaf, cancellationToken).ConfigureAwait(false));
        return Correlate(sourceFiles, identities);
    }

    public static IReadOnlyList<MigrationCorrelationResult> Correlate(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<ImportedArticleIdentity> importedLeaves) =>
        sourceFiles.Select(source => CorrelateOne(source, importedLeaves)).ToArray();

    private static MigrationCorrelationResult CorrelateOne(
        MigrationReleaseFile source,
        IReadOnlyList<ImportedArticleIdentity> importedLeaves)
    {
        if (string.IsNullOrWhiteSpace(source.SourceFileId)
            || string.IsNullOrWhiteSpace(source.ArticleIdentityKind)
            || string.IsNullOrWhiteSpace(source.ArticleIdentityDigest))
            return Result(source, "missing-source", null, null, []);
        var strong = importedLeaves.Where(target =>
                target.FileSize == source.FileSize
                && target.IdentityKind == source.ArticleIdentityKind
                && target.IdentityDigest == source.ArticleIdentityDigest)
            .ToArray();
        if (strong.Length == 0)
            return Result(source, "unmatched-target", null, null, []);
        if (strong.Length == 1)
            return Result(source, "exact", strong[0].DavItemId, strong[0].NzbBlobId, strong);
        var pathMatches = strong.Where(target =>
                MatchKey.ForLeaf(target.Name) == source.NormalisedName
                || MatchKey.ForRelativePath(target.Path) == MatchKey.ForRelativePath(source.VirtualPath))
            .ToArray();
        if (pathMatches.Length == 1)
            return Result(source, "exact", pathMatches[0].DavItemId, pathMatches[0].NzbBlobId, strong);
        var equivalent = strong.Select(target => (MatchKey.ForLeaf(target.Name), target.FileSize)).Distinct().Count() == 1;
        return Result(source, equivalent ? "duplicate" : "ambiguous", null, null, strong);
    }

    private static MigrationCorrelationResult Result(
        MigrationReleaseFile source,
        string status,
        Guid? id,
        Guid? blob,
        IReadOnlyCollection<ImportedArticleIdentity> candidates) =>
        new(source.Id, status, id, blob, status == "exact" ? "article-identity" : null, JsonSerializer.Serialize(new
        {
            source.SourceFileId,
            source.ArticleIdentityKind,
            source.ArticleIdentityDigest,
            source.FileSize,
            candidates = candidates.Select(candidate => candidate.DavItemId),
        }));
}
