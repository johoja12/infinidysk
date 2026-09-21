using System.Text.Json;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Naming;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.Utils;

namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed record NzbDavCorrelationScope(
    long RunId,
    string SourceReleaseId,
    Guid ImportedNzoId,
    bool AllowLegacyUniqueSizeFallback);

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

    public async Task<IReadOnlyList<MigrationCorrelationResult>> CorrelateAsync(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<DavItem> importedLeaves,
        DavDatabaseContext davContext,
        NzbDavCorrelationScope scope,
        CancellationToken cancellationToken = default)
    {
        _ = davContext;
        var identities = new List<ImportedArticleIdentity>(importedLeaves.Count * 2);
        foreach (var leaf in importedLeaves)
        {
            identities.Add(await identityReader.ReadAsync(leaf, cancellationToken).ConfigureAwait(false));
            var legacy = await identityReader.ReadLegacyArchiveAsync(leaf, cancellationToken).ConfigureAwait(false);
            if (legacy.IdentityKind is not null)
                identities.Add(legacy);
        }
        return Correlate(sourceFiles, identities, scope);
    }

    public static IReadOnlyList<MigrationCorrelationResult> Correlate(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<ImportedArticleIdentity> importedLeaves) =>
        sourceFiles.Select(source => CorrelateOne(source, importedLeaves, null)).ToArray();

    public static IReadOnlyList<MigrationCorrelationResult> Correlate(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<ImportedArticleIdentity> importedLeaves,
        NzbDavCorrelationScope scope) =>
        sourceFiles.Select(source => CorrelateOne(source, importedLeaves, scope)).ToArray();

    private static MigrationCorrelationResult CorrelateOne(
        MigrationReleaseFile source,
        IReadOnlyList<ImportedArticleIdentity> importedLeaves,
        NzbDavCorrelationScope? scope)
    {
        if (string.IsNullOrWhiteSpace(source.SourceFileId)
            || string.IsNullOrWhiteSpace(source.ArticleIdentityKind)
            || string.IsNullOrWhiteSpace(source.ArticleIdentityDigest))
            return Result(source, "missing-source", null, null, null, []);
        var strong = importedLeaves.Where(target =>
                target.FileSize == source.FileSize
                && target.IdentityKind == source.ArticleIdentityKind
                && target.IdentityDigest == source.ArticleIdentityDigest)
            .ToArray();
        if (strong.Length == 0)
            return LegacyFallback(source, importedLeaves, scope);
        if (strong.Length == 1)
            return Result(source, "exact", strong[0].DavItemId, strong[0].NzbBlobId,
                source.ArticleIdentityKind == NzbDavArticleIdentity.ArchiveMemberKind
                    ? "archive-path-in-archive-v1"
                    : "article-identity",
                strong);
        var pathMatches = strong.Where(target =>
                MatchKey.ForLeaf(target.Name) == source.NormalisedName
                || MatchKey.ForRelativePath(target.Path) == MatchKey.ForRelativePath(source.VirtualPath))
            .ToArray();
        if (pathMatches.Length == 1)
            return Result(source, "exact", pathMatches[0].DavItemId, pathMatches[0].NzbBlobId,
                source.ArticleIdentityKind == NzbDavArticleIdentity.ArchiveMemberKind
                    ? "archive-path-in-archive-v1"
                    : "article-identity",
                strong);
        var equivalent = strong.Select(target => (MatchKey.ForLeaf(target.Name), target.FileSize)).Distinct().Count() == 1;
        return Result(source, equivalent ? "duplicate" : "ambiguous", null, null, null, strong);
    }

    private static MigrationCorrelationResult LegacyFallback(
        MigrationReleaseFile source,
        IReadOnlyList<ImportedArticleIdentity> importedLeaves,
        NzbDavCorrelationScope? scope)
    {
        if (scope is not { AllowLegacyUniqueSizeFallback: true }
            || source.ArticleIdentityKind != NzbDavArticleIdentity.ArchiveMemberKind)
            return Result(source, "unmatched-target", null, null, null, []);
        var candidates = importedLeaves
            .Where(target => target.FileSize == source.FileSize
                             && target.NzbBlobId == scope.ImportedNzoId)
            .GroupBy(target => target.DavItemId)
            .Select(group => group.First())
            .ToArray();
        if (candidates.Length == 1 && FilenameUtil.IsVideoFile(candidates[0].Name))
            return Result(source, "exact", candidates[0].DavItemId, candidates[0].NzbBlobId,
                "legacy-release-payload-size-unique", candidates);
        return Result(source, candidates.Length > 1 ? "ambiguous" : "unmatched-target",
            null, null, null, candidates);
    }

    private static MigrationCorrelationResult Result(
        MigrationReleaseFile source,
        string status,
        Guid? id,
        Guid? blob,
        string? matchMethod,
        IReadOnlyCollection<ImportedArticleIdentity> candidates) =>
        new(source.Id, status, id, blob, status == "exact" ? matchMethod : null, JsonSerializer.Serialize(new
        {
            source.SourceFileId,
            source.ArticleIdentityKind,
            source.ArticleIdentityDigest,
            source.FileSize,
            candidates = candidates.Select(candidate => candidate.DavItemId),
        }));
}
