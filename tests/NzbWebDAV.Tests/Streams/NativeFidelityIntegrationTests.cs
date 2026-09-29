using NzbWebDAV.Models;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

public sealed class NativeFidelityIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SequentialCacheBlocks_KeepSegmentPipelineWithoutRefetchingBoundaries(bool multipart, bool cancelEarly)
    {
        var directory = Directory.CreateTempSubdirectory("native-pipeline-");
        try
        {
            const int segmentSize = 1024 * 1024;
            var ids = Enumerable.Range(0, 12).Select(i => $"segment-{i}").ToArray();
            var data = ids.Select((id, i) => (id, bytes: Enumerable.Repeat((byte)i, segmentSize).ToArray()))
                .ToDictionary(pair => pair.id, pair => pair.bytes);
            var ranges = Enumerable.Range(0, ids.Length)
                .Select(i => new LongRange((long)(multipart ? i % 6 : i) * segmentSize,
                    (long)((multipart ? i % 6 : i) + 1) * segmentSize)).ToArray();
            using var client = new FakeNntpClient(data, useCachedYencStreams: true,
                segmentRanges: ids.Zip(ranges).ToDictionary(pair => pair.First, pair => pair.Second));
            await using var store = new NativeCacheStore(Path.Combine(directory.FullName, "index.db"),
                [new NativeCacheFolder { Path = directory.FullName, MinFreeBytes = 0 }]);
            var identity = new NativeCacheIdentity("pipeline", "v1", (long)ids.Length * segmentSize);
            var readSegments = cancelEarly ? 4 : ids.Length;
            Stream OpenSource() => multipart
                ? new DavMultipartFileStream(new DavMultipartFile
                {
                    Id = Guid.NewGuid(),
                    Metadata = new DavMultipartFile.Meta
                    {
                        FileParts = Enumerable.Range(0, 2).Select(part => new DavMultipartFile.FilePart
                        {
                            SegmentIds = ids.Skip(part * 6).Take(6).ToArray(),
                            SegmentIdByteRange = new LongRange(0, 6L * segmentSize),
                            FilePartByteRange = new LongRange(0, 6L * segmentSize),
                            SegmentByteRanges = ranges.Skip(part * 6).Take(6).ToArray(),
                            SegmentByteRangesTrusted = true,
                        }).ToArray(),
                    },
                }, client, 40, resolver: null, usePipelinedBodyRequests: true)
                : new NzbFileStream(ids, identity.Length, client, 40, ranges,
                    usePipelinedBodyRequests: true, segmentByteRangesTrusted: true);
            await using (var stream = new NativeCachedStream(store, identity,
                _ => Task.FromResult(OpenSource()), () => true))
            {
                using var cancellation = new CancellationTokenSource();
                var actual = new byte[segmentSize];
                for (var i = 0; i < readSegments; i++)
                {
                    await stream.ReadExactlyAsync(actual, cancellation.Token);
                    Assert.Equal(data[ids[i]], actual);
                }
                if (cancelEarly)
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => stream.ReadAsync(actual, cancellation.Token).AsTask());
                }
            }
            Assert.Equal((long)readSegments * segmentSize, await store.GetCoverageAsync(identity));
            Assert.InRange(client.BodyRequestCounts.Count, readSegments, ids.Length);
            Assert.All(client.BodyRequestCounts.Values, count => Assert.Equal(1, count));
            Assert.Equal(client.BodyRequestCount, client.CompletionCallbackCount);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ThreeSpeculativeHoles_DoNotPoisonReadablePlayerPrefix()
    {
        var directory = Directory.CreateTempSubdirectory("native-prefix-");
        try
        {
            var ranges = new LongRange[] { new(0, 5), new(5, 10), new(10, 15), new(15, 20), new(20, 25) };
            using var client = new FakeNntpClient(new Dictionary<string, byte[]> { ["one"] = "abcde"u8.ToArray(), ["five"] = "uvwxy"u8.ToArray() },
                useCachedYencStreams: true, segmentRanges: new Dictionary<string, LongRange> { ["one"] = ranges[0], ["five"] = ranges[4] });
            await using var store = new NativeCacheStore(Path.Combine(directory.FullName, "index.db"),
                [new NativeCacheFolder { Path = directory.FullName, MinFreeBytes = 0 }]);
            var fileName = "/native-prefix-" + Guid.NewGuid().ToString("N");
            await using var stream = new NativeCachedStream(store, new("file", "v1", 25),
                _ => Task.FromResult<Stream>(new NzbFileStream(["one", "two", "three", "four", "five"], 25,
                    client, 0, ranges, fileName: fileName, segmentByteRangesTrusted: true)), () => true);
            var prefix = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(prefix));
            Assert.Equal((byte)'a', prefix[0]);
            Assert.False(PlaybackHoleTracker.ShouldFailFast(fileName, out _));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("complete", 15)]
    [InlineData("missing", 0)]
    [InlineData("short", 0)]
    [InlineData("wrong-offset", 0)]
    public async Task NativeStorage_AdmitsOnlyCompleteVerifiedFinalFileBytes(string middleKind, long coverage)
    {
        var directory = Directory.CreateTempSubdirectory("native-fidelity-");
        try
        {
            var data = new Dictionary<string, byte[]>
            {
                ["one"] = "abcde"u8.ToArray(), ["three"] = "klmno"u8.ToArray(),
            };
            if (middleKind != "missing")
                data["two"] = middleKind == "short" ? "fg"u8.ToArray() : "fghij"u8.ToArray();
            var ranges = new LongRange[] { new(0, 5), new(5, 10), new(10, 15) };
            using var client = new FakeNntpClient(data, useCachedYencStreams: true,
                segmentRanges: new Dictionary<string, LongRange>
                {
                    ["one"] = ranges[0],
                    ["two"] = middleKind == "wrong-offset" ? new LongRange(50, 55) : ranges[1],
                    ["three"] = ranges[2],
                });
            await using var store = new NativeCacheStore(Path.Combine(directory.FullName, "index.db"),
                [new NativeCacheFolder { Path = directory.FullName, MinFreeBytes = 0 }]);
            var identity = new NativeCacheIdentity("file", "generation", 15);
            await using (var stream = new NativeCachedStream(store, identity,
                _ => Task.FromResult<Stream>(new NzbFileStream(["one", "two", "three"], 15, client, 0,
                    ranges, fileName: "native-fidelity-" + Guid.NewGuid().ToString("N"),
                    segmentByteRangesTrusted: true)), () => true))
            {
                var bytes = new byte[15];
                await stream.ReadExactlyAsync(bytes);
                Assert.Equal("abcde"u8.ToArray(), bytes[..5]);
                Assert.Equal("klmno"u8.ToArray(), bytes[10..]);
            }
            Assert.Equal(coverage, await store.GetCoverageAsync(identity));
            if (coverage > 0)
            {
                await using var hit = new NativeCachedStream(store, identity,
                    _ => throw new InvalidOperationException("Verified cache hit opened the source."), () => true);
                var bytes = new byte[15];
                await hit.ReadExactlyAsync(bytes);
                Assert.Equal("abcdefghijklmno"u8.ToArray(), bytes);
            }
        }
        finally { directory.Delete(recursive: true); }
    }
}
