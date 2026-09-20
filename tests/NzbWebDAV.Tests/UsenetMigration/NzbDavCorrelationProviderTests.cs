using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Provenance;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavCorrelationProviderTests
{
    [Fact]
    public void Correlate_RequiresIdentityKindDigestAndExactSize()
    {
        var source = Source();
        var exact = Target(Guid.NewGuid(), "episode.mkv", 100, source.ArticleIdentityDigest!);
        var wrongSize = Target(Guid.NewGuid(), "episode.mkv", 101, source.ArticleIdentityDigest!);
        var wrongDigest = Target(Guid.NewGuid(), "episode.mkv", 100, new string('b', 64));

        var result = Assert.Single(NzbDavCorrelationProvider.Correlate(
            [source], [wrongSize, wrongDigest, exact]));

        Assert.Equal("exact", result.Status);
        Assert.Equal(exact.DavItemId, result.DavItemId);
        Assert.Equal("article-identity", result.MatchMethod);
    }

    [Fact]
    public void Correlate_ClassifiesMissingUnmatchedDuplicateAndAmbiguous()
    {
        var source = Source();
        var missing = Source();
        missing.Id = 2;
        missing.SourceFileId = null;
        var duplicateTargets = new[]
        {
            Target(Guid.NewGuid(), "episode.mkv", 100, source.ArticleIdentityDigest!),
            Target(Guid.NewGuid(), "episode.mkv", 100, source.ArticleIdentityDigest!),
        };
        var ambiguousTargets = new[]
        {
            Target(Guid.NewGuid(), "other-a.mkv", 100, source.ArticleIdentityDigest!),
            Target(Guid.NewGuid(), "other-b.mkv", 100, source.ArticleIdentityDigest!),
        };

        Assert.Equal("missing-source", Assert.Single(NzbDavCorrelationProvider.Correlate([missing], [])).Status);
        Assert.Equal("unmatched-target", Assert.Single(NzbDavCorrelationProvider.Correlate([source], [])).Status);
        Assert.Equal("duplicate", Assert.Single(NzbDavCorrelationProvider.Correlate([source], duplicateTargets)).Status);
        Assert.Equal("ambiguous", Assert.Single(NzbDavCorrelationProvider.Correlate([source], ambiguousTargets)).Status);
    }

    [Fact]
    public void Correlate_UsesNameOnlyToDisambiguateStrongCandidates()
    {
        var source = Source();
        var expected = Target(Guid.NewGuid(), "episode.mkv", 100, source.ArticleIdentityDigest!);
        var other = Target(Guid.NewGuid(), "other.mkv", 100, source.ArticleIdentityDigest!);

        var result = Assert.Single(NzbDavCorrelationProvider.Correlate([source], [other, expected]));

        Assert.Equal("exact", result.Status);
        Assert.Equal(expected.DavItemId, result.DavItemId);
    }

    private static MigrationReleaseFile Source() => new()
    {
        Id = 1,
        SourceFileId = Guid.NewGuid().ToString(),
        VirtualPath = "/content/show/episode.mkv",
        FileName = "episode.mkv",
        NormalisedName = "episode.mkv",
        FileSize = 100,
        ArticleIdentityKind = NzbDavArticleIdentity.DirectKind,
        ArticleIdentityDigest = new string('a', 64),
    };

    private static ImportedArticleIdentity Target(Guid id, string name, long size, string digest) =>
        new(id, name, $"/content/migration/job/{name}", size,
            NzbDavArticleIdentity.DirectKind, digest, Guid.NewGuid());
}
