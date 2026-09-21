using System.Text;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Models;
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

    [Fact]
    public async Task IdentityReader_UsesStableRangesForEagerMultipartAndRejectsUnresolvedPendingParts()
    {
        var store = new MemoryBlobStore();
        var nzbBlobId = Guid.NewGuid();
        var eagerBlobId = Guid.NewGuid();
        var lazyBlobId = Guid.NewGuid();
        await ((IBlobStore)store).WriteBlob(nzbBlobId, new MemoryStream(Encoding.UTF8.GetBytes(Nzb(
            (1, 100, "one@test"),
            (2, 120, "two@test")))));
        await store.WriteBlob(eagerBlobId, new DavMultipartFile
        {
            Id = eagerBlobId,
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["one@test", "two@test"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 220),
                        FilePartByteRange = LongRange.FromStartAndSize(10, 80),
                    },
                ],
            },
        });
        await store.WriteBlob(lazyBlobId, new DavMultipartFile
        {
            Id = lazyBlobId,
            Metadata = new DavMultipartFile.Meta
            {
                IsLazy = true,
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["one@test"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 100),
                        FilePartByteRange = LongRange.FromStartAndSize(10, 80),
                    },
                ],
                PendingParts =
                [
                    new DavMultipartFile.PendingPart
                    {
                        SegmentIds = ["two@test"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 120),
                        EstimatedDataSize = 100,
                    },
                ],
            },
        });
        var reader = new DavItemArticleIdentityReader(store);

        var eager = await reader.ReadAsync(Item(eagerBlobId, nzbBlobId, "renamed.mkv", 80));
        var unresolved = await reader.ReadAsync(Item(lazyBlobId, nzbBlobId, "lazy.mkv", 180));

        Assert.Equal(NzbDavStableArchiveIdentity.Kind, eager.IdentityKind);
        Assert.NotNull(eager.IdentityDigest);
        Assert.Null(unresolved.IdentityKind);
        Assert.Null(unresolved.IdentityDigest);
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

    private static DavItem Item(Guid fileBlobId, Guid nzbBlobId, string name, long size) => new()
    {
        Id = Guid.NewGuid(),
        IdPrefix = "abcde",
        CreatedAt = DateTime.UtcNow,
        Name = name,
        Path = $"/content/migration/release/{name}",
        FileSize = size,
        Type = DavItem.ItemType.UsenetFile,
        SubType = DavItem.ItemSubType.MultipartFile,
        FileBlobId = fileBlobId,
        NzbBlobId = nzbBlobId,
    };

    private static string Nzb(params (int Number, long Bytes, string MessageId)[] segments) =>
        "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"><file poster=\"test\" date=\"0\" subject=\"archive\">"
        + "<groups><group>alt.binaries.test</group></groups><segments>"
        + string.Concat(segments.Select(segment =>
            $"<segment bytes=\"{segment.Bytes}\" number=\"{segment.Number}\">{segment.MessageId}</segment>"))
        + "</segments></file></nzb>";

    private sealed class MemoryBlobStore : IBlobStore
    {
        private readonly Dictionary<Guid, byte[]> _raw = [];
        private readonly Dictionary<Guid, object> _metadata = [];

        public async Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default)
        {
            await using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken);
            _raw[id] = copy.ToArray();
        }

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _metadata[id] = blob!;
            return Task.CompletedTask;
        }

        public Stream? ReadBlob(Guid id) =>
            _raw.TryGetValue(id, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

        public Task<T?> ReadBlob<T>(Guid id) =>
            Task.FromResult(_metadata.TryGetValue(id, out var value) ? (T?)value : default);

        public bool Exists(Guid id) => _raw.ContainsKey(id) || _metadata.ContainsKey(id);
        public bool Delete(Guid id) => _raw.Remove(id) | _metadata.Remove(id);
    }
}
