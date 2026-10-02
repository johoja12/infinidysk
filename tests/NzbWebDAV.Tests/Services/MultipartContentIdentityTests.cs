using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using static NzbWebDAV.Tests.Fakes.Rar4TestArchiveBuilder;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class MultipartContentIdentityTests : IDisposable
{
    private const string PathInArchive = "movie.mkv";
    private readonly string _root = Path.Join(Path.GetTempPath(), $"multipart-identity-{Guid.NewGuid():N}");
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");
    private readonly FileBlobStore _blobs = new();
    private readonly FakeNntpClient _client = new(new Dictionary<string, byte[]>());

    public MultipartContentIdentityTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        BlobStore.Use(_blobs);
    }

    public void Dispose()
    {
        _blobs.Dispose();
        _client.Dispose();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _oldConfig);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task LazyResolution_KeepsIdentityOfPendingState()
    {
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2"] = BuildRar4ContinuationVolume(PathInArchive, packedSize: 500, splitAfter: true),
            ["vol3"] = BuildRar4ContinuationVolume(PathInArchive, packedSize: 400),
        };
        var mpf = Lazy(volumes, ("vol2", 500), ("vol3", 400));
        var pending = MultipartContentIdentity.Compute(mpf.Metadata);
        var resolver = Resolver(volumes);

        var partial = await resolver.ResolveNextAsync(mpf, CancellationToken.None);
        Assert.True(partial.IsLazy);
        Assert.Equal(pending, MultipartContentIdentity.Get(partial));

        var complete = await resolver.ResolveNextAsync(mpf, CancellationToken.None);
        Assert.False(complete.IsLazy);
        Assert.Equal(3, complete.FileParts.Length);
        Assert.NotEqual(pending, MultipartContentIdentity.Compute(complete));
        Assert.Equal(pending, MultipartContentIdentity.Get(complete));
    }

    [Fact]
    public async Task LazyResolution_DroppingUnrelatedTrailingVolumes_KeepsIdentity()
    {
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2"] = BuildRar4ContinuationVolume(PathInArchive, packedSize: 500),
            ["vol3"] = BuildRar4Volume("extra.srt", packedSize: 100),
        };
        var mpf = Lazy(volumes, ("vol2", 500), ("vol3", 20));
        mpf.Metadata.ExpectedFileSize = 940 + 500;
        var pending = MultipartContentIdentity.Compute(mpf.Metadata);

        var meta = await Resolver(volumes).EnsureResolvedThroughAsync(mpf, long.MaxValue, CancellationToken.None);

        Assert.False(meta.IsLazy);
        Assert.Empty(meta.PendingParts);
        Assert.Equal(pending, MultipartContentIdentity.Get(meta));
    }

    [Fact]
    public async Task PersistedPartialResolution_RoundTripsCarriedIdentity()
    {
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2"] = BuildRar4ContinuationVolume(PathInArchive, packedSize: 500, splitAfter: true),
            ["vol3"] = BuildRar4ContinuationVolume(PathInArchive, packedSize: 400),
        };
        var mpf = Lazy(volumes, ("vol2", 500), ("vol3", 400));
        var pending = MultipartContentIdentity.Compute(mpf.Metadata);
        await Resolver(volumes).ResolveNextAsync(mpf, CancellationToken.None);

        // The resolver persists in the background; read the blob back from disk.
        DavMultipartFile? restored = null;
        for (var attempt = 0; attempt < 250 && restored?.Metadata.ContentIdentity is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(20);
            using var fresh = new FileBlobStore();
            restored = await fresh.ReadBlob<DavMultipartFile>(mpf.Id);
        }

        Assert.NotNull(restored);
        Assert.True(restored!.Metadata.IsLazy);
        Assert.Equal(pending, restored.Metadata.ContentIdentity);
        Assert.Equal(pending, MultipartContentIdentity.Get(restored.Metadata));
    }

    [Fact]
    public void ContentDefiningChanges_ProduceDifferentIdentity()
    {
        var baseline = MultipartContentIdentity.Compute(Complete().Metadata);
        Assert.Equal(baseline, MultipartContentIdentity.Compute(Complete().Metadata));

        var changes = new Action<DavMultipartFile.Meta>[]
        {
            meta => meta.FileParts[1].SegmentIds = ["other-vol2"],
            meta => meta.FileParts[1].FilePartByteRange = LongRange.FromStartAndSize(50, 450),
            meta => meta.FileParts[1].SegmentIdByteRange = LongRange.FromStartAndSize(0, 600),
            meta => meta.FileParts[0].SegmentByteRanges = [LongRange.FromStartAndSize(0, 1_000)],
            meta => meta.FileParts = [meta.FileParts[1], meta.FileParts[0]],
            meta => meta.ExpectedFileSize = 1,
            meta => meta.PathInArchive = "other.mkv",
            meta => meta.AesParams = new AesParams { DecodedSize = 1, Iv = [1], Key = [2] },
            meta => meta.PendingParts = [new DavMultipartFile.PendingPart { SegmentIds = ["vol3"] }],
        };
        foreach (var change in changes)
        {
            var changed = Complete();
            change(changed.Metadata);
            Assert.NotEqual(baseline, MultipartContentIdentity.Compute(changed.Metadata));
        }
    }

    [Fact]
    public void NonContentMetadata_DoesNotChangeIdentity()
    {
        var baseline = MultipartContentIdentity.Compute(Complete().Metadata);
        var changed = Complete();
        changed.Metadata.IsLazy = true;
        changed.Metadata.ArchivePassword = "secret";
        changed.Metadata.FileParts[0].IsSplitAfter = null;
        changed.Metadata.FileParts[1].SegmentFallbackIds = [["fallback"]];
        changed.Metadata.FileParts[1].SegmentByteRangesTrusted = true;
        changed.Metadata.FileParts[1].VerificationProof = new Par2FileProof();
        Assert.Equal(baseline, MultipartContentIdentity.Compute(changed.Metadata));
    }

    private static DavMultipartFile Complete() => new()
    {
        Id = Guid.Empty,
        Metadata = new DavMultipartFile.Meta
        {
            PathInArchive = PathInArchive,
            ExpectedFileSize = 1_440,
            FileParts =
            [
                new DavMultipartFile.FilePart
                {
                    SegmentIds = ["vol1"],
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, 1_000),
                    FilePartByteRange = LongRange.FromStartAndSize(60, 940),
                    IsSplitAfter = true,
                },
                new DavMultipartFile.FilePart
                {
                    SegmentIds = ["vol2"],
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, 560),
                    FilePartByteRange = LongRange.FromStartAndSize(60, 500),
                    IsSplitAfter = false,
                },
            ],
        },
    };

    private static DavMultipartFile Lazy(
        IReadOnlyDictionary<string, byte[]> volumes,
        params (string Segment, long DataSize)[] pending) => new()
    {
        Id = Guid.NewGuid(),
        Metadata = new DavMultipartFile.Meta
        {
            IsLazy = true,
            PathInArchive = PathInArchive,
            ExpectedFileSize = 940 + pending.Sum(part => part.DataSize),
            FileParts =
            [
                new DavMultipartFile.FilePart
                {
                    SegmentIds = ["vol1"],
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, 1_000),
                    FilePartByteRange = LongRange.FromStartAndSize(60, 940),
                    IsSplitAfter = true,
                }
            ],
            PendingParts = pending.Select(part => new DavMultipartFile.PendingPart
            {
                SegmentIds = [part.Segment],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, volumes[part.Segment].Length),
                EstimatedDataSize = part.DataSize,
            }).ToArray(),
        },
    };

    private LazyRarResolver Resolver(IReadOnlyDictionary<string, byte[]> volumes) =>
        new(_client, new ConfigManager())
        {
            VolumeStreamFactory = (ids, _) => new MemoryStream(volumes[ids[0]], writable: false),
            ReconcileFileSizeAsync = (_, _, _) => Task.CompletedTask,
        };
}
