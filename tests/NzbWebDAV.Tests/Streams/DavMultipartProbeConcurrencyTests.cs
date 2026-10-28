using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

public sealed class DavMultipartProbeConcurrencyTests
{
    [Fact]
    public async Task ColdGeometry_TwoProbesOverlap_ThirdWaitsForAnAvailableSlot()
    {
        using var native = new NativeCacheReadContext();
        using var client = CreateClient();
        await using var stream = CreateStream(client);
        var output = new byte[12];
        var read = stream.ReadExactlyAsync(output).AsTask();
        try
        {
            await client.Started("seg-0").WaitAsync(TimeSpan.FromSeconds(5));
            await client.Started("seg-1").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(client.Started("seg-2").IsCompleted);
            client.Release("seg-1");
            await client.Started("seg-2").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(read.IsCompleted);
            Assert.Equal(2, client.Maximum);
            client.ReleaseAll();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Enumerable.Range(1, 12).Select(x => (byte)x), output);
            Assert.True(stream.LastReadCacheable);
            Assert.Equal(0, client.Active);
        }
        finally { client.ReleaseAll(); }
    }

    [Fact]
    public async Task GenerationCancellation_JoinsActiveProbesWithoutStartingQueuedProbe()
    {
        using var native = new NativeCacheReadContext();
        using var client = CreateClient();
        await using var stream = CreateStream(client);
        var read = stream.ReadAsync(new byte[12]).AsTask();
        try
        {
            await client.Started("seg-1").WaitAsync(TimeSpan.FromSeconds(5));
            stream.Seek(0, SeekOrigin.Begin);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, client.Active);
            Assert.False(client.Started("seg-2").IsCompleted);
            Assert.False(stream.LastReadCacheable);
        }
        finally { client.ReleaseAll(); }
    }

    [Fact]
    public async Task ProbeFailure_CancelsSibling_AndPreservesPlaybackFallback()
    {
        using var native = new NativeCacheReadContext();
        using var client = CreateClient();
        await using var stream = CreateStream(client);
        var output = new byte[4];
        var read = stream.ReadAsync(output).AsTask();
        try
        {
            await client.Started("seg-1").WaitAsync(TimeSpan.FromSeconds(5));
            client.Fail("seg-0");
            Assert.Equal(output.Length, await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, output);
            Assert.False(stream.LastReadCacheable);
            Assert.Equal(0, client.Active);
            Assert.False(client.Started("seg-2").IsCompleted);
        }
        finally { client.ReleaseAll(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SmallVolumes_ProbeEachArticleOnce(int count)
    {
        using var native = new NativeCacheReadContext();
        using var client = CreateClient(count);
        client.ReleaseAll();
        await using var stream = CreateStream(client, count);
        var output = new byte[count * 4];
        await stream.ReadExactlyAsync(output);
        Assert.Equal(Enumerable.Range(1, count * 4).Select(x => (byte)x), output);
        Assert.True(stream.LastReadCacheable);
        Assert.Equal(count, client.HeaderRequests);
    }

    private static ProbeClient CreateClient(int count = 3)
    {
        var data = Enumerable.Range(0, count).ToDictionary(i => $"seg-{i}",
            i => Enumerable.Range(i * 4 + 1, 4).Select(x => (byte)x).ToArray());
        var ranges = Enumerable.Range(0, count).ToDictionary(i => $"seg-{i}", i => new LongRange(i * 4, (i + 1) * 4));
        return new ProbeClient(new FakeNntpClient(data, useCachedYencStreams: true, segmentRanges: ranges), count);
    }

    private static DavMultipartFileStream CreateStream(INntpClient client, int count = 3) => new(new DavMultipartFile
    {
        Id = Guid.NewGuid(), Metadata = new DavMultipartFile.Meta
        {
            FileParts = [new DavMultipartFile.FilePart
            {
                SegmentIds = Enumerable.Range(0, count).Select(i => $"seg-{i}").ToArray(),
                SegmentIdByteRange = new LongRange(0, count * 4), FilePartByteRange = new LongRange(0, count * 4),
            }],
        },
    }, client, 0, resolver: null, usePipelinedBodyRequests: true);

    private sealed class ProbeClient : WrappingNntpClient
    {
        private readonly Dictionary<string, TaskCompletionSource> _started;
        private readonly Dictionary<string, TaskCompletionSource> _release;
        private readonly Lock _sync = new();
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public int Maximum { get; private set; }
        public int HeaderRequests { get; private set; }
        public ProbeClient(INntpClient inner, int count) : base(inner)
        {
            _started = Enumerable.Range(0, count).ToDictionary(i => $"seg-{i}", _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _release = _started.Keys.ToDictionary(k => k, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        public Task Started(string id) => _started[id].Task;
        public void Release(string id) => _release[id].TrySetResult();
        public void ReleaseAll() { foreach (var gate in _release.Values) gate.TrySetResult(); }
        public void Fail(string id) => _release[id].TrySetException(new IOException("Probe unavailable"));
        public override async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            lock (_sync) { HeaderRequests++; Maximum = Math.Max(Maximum, Interlocked.Increment(ref _active)); }
            _started[segmentId].TrySetResult();
            try
            {
                await _release[segmentId].Task.WaitAsync(ct);
                Task<UsenetYencHeader> result;
                lock (_sync) result = base.GetYencHeadersAsync(segmentId, ct);
                return await result;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
