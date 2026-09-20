using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Services;

public sealed class NativePrefetchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-warm-" + Guid.NewGuid().ToString("N"));
    private NativeCacheStore _store = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        _store = new NativeCacheStore(Path.Combine(_root, "index", "cache.db"),
            [new NativeCacheFolder { Id = "media", Path = Path.Combine(_root, "media"), MinFreeBytes = 0 }]);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WholeFileWarm_SkipsCommittedBlocksAndCompletesOnlyVerifiedCoverage()
    {
        var bytes = new byte[NativeCacheStore.BlockSize + 3];
        bytes[^1] = 7;
        var identity = new NativeCacheIdentity("movie", "revision", bytes.Length);
        Assert.True(await _store.WriteBlockAsync(identity, 0, bytes.AsMemory(0, NativeCacheStore.BlockSize)));
        var source = new VerifiedSource(bytes, true);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);
        var charged = 0L;
        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, count => { charged += count; return true; }, _ => { }, CancellationToken.None);
        Assert.Equal(3, source.ReadBytes);
        Assert.Equal(3, charged);
        Assert.Equal(bytes.Length, await _store.GetCoverageAsync(identity));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnverifiedOrOverBudgetWork_DoesNotComplete(bool verified, bool budget)
    {
        var identity = new NativeCacheIdentity("movie", "revision", 3);
        var source = new VerifiedSource([1, 2, 3], verified);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);
        await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            _ => budget, _ => { }, CancellationToken.None));
        Assert.Equal(0, await _store.GetCoverageAsync(identity));
        if (!budget) Assert.Equal(0, source.ReadBytes);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private sealed class VerifiedSource(byte[] bytes, bool verified) : MemoryStream(bytes), ICacheReadEvidence
    {
        public long ReadBytes { get; private set; }
        public bool LastReadCacheable => verified;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += read;
            return read;
        }
    }
}
