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
    public async Task PartialWarm_ReservesRequestedBlocksRatherThanEntireMovie()
    {
        var folder = Path.Combine(_root, "small");
        Directory.CreateDirectory(folder);
        await using var store = new NativeCacheStore(Path.Combine(_root, "small.db"),
            [new NativeCacheFolder { Id = "small", Path = folder, MinFreeBytes = 0, MaxBytes = 8L * 1024 * 1024 }]);
        var identity = new NativeCacheIdentity("large-movie", "revision", 16L * 1024 * 1024);
        await using var stream = new NativeCachedStream(store, identity,
            _ => Task.FromResult<Stream>(new VerifiedSource(new byte[identity.Length], true)), () => true);
        await NativePrefetchExecutor.WarmAsync(store, stream, 0, 1, _ => true, _ => { }, CancellationToken.None);
        Assert.Equal(NativeCacheStore.BlockSize, await store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task FullyCachedReadOnlyWarm_RequiresNoWritableReservationOrBudget()
    {
        var identity = new NativeCacheIdentity("movie", "revision", 3);
        Assert.True(await _store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        await _store.DisposeAsync();
        _store = new NativeCacheStore(Path.Combine(_root, "index", "cache.db"),
            [new NativeCacheFolder { Id = "media", Path = Path.Combine(_root, "media"), ReadOnly = true, MinFreeBytes = 0 }]);
        await using var stream = new NativeCachedStream(_store, identity, _ => throw new InvalidOperationException("Opened source"), () => true);
        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, (Func<long, bool>)(_ => throw new InvalidOperationException("Spent budget")), _ => { }, CancellationToken.None);
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
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("corrupt")]
    public async Task Warm_RechecksCachedDataBeforeDeclaringCompletion(string damage)
    {
        var identity = new NativeCacheIdentity("damaged-movie", "revision", 3);
        byte[] expected = [1, 2, 3];
        Assert.True(await _store.WriteBlockAsync(identity, 0, expected));
        var path = Path.Combine(_root, "media", "v1", identity.Key[..2], identity.Key, "content.data");
        if (damage == "missing") File.Delete(path);
        else await File.WriteAllBytesAsync(path, damage == "truncated" ? [1] : [9, 9, 9]);

        var source = new VerifiedSource(expected, true);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);
        var charged = 0L;
        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            count => { charged += count; return true; }, _ => { }, CancellationToken.None);

        Assert.Equal(3, source.ReadBytes);
        Assert.Equal(3, charged);
        var actual = new byte[3];
        Assert.Equal(3, await _store.ReadBlockAsync(identity, 0, actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Warm_OfflineCachedVolumeCannotReportCompletion()
    {
        var identity = new NativeCacheIdentity("offline-movie", "revision", 3);
        Assert.True(await _store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        Directory.Move(Path.Combine(_root, "media"), Path.Combine(_root, "offline-media"));
        await using var stream = new NativeCachedStream(_store, identity,
            _ => throw new InvalidOperationException("Offline cache opened source"), () => true);

        await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            (Func<long, bool>)(_ => throw new InvalidOperationException("Offline cache spent budget")), _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Warm_RepairsCorruptMiddleWithoutDownloadingValidHeadAndTail()
    {
        var block = NativeCacheStore.BlockSize;
        var bytes = new byte[block * 2 + 3];
        new Random(826).NextBytes(bytes);
        var identity = new NativeCacheIdentity("middle-damage", "revision", bytes.Length);
        Assert.True(await _store.WriteBlockAsync(identity, 0, bytes.AsMemory(0, block)));
        Assert.True(await _store.WriteBlockAsync(identity, block, bytes.AsMemory(block, block)));
        Assert.True(await _store.WriteBlockAsync(identity, block * 2L, bytes.AsMemory(block * 2)));
        var path = Path.Combine(_root, "media", "v1", identity.Key[..2], identity.Key, "content.data");
        await using (var file = File.OpenWrite(path))
        {
            file.Position = block;
            await file.WriteAsync(new byte[block]);
        }

        var source = new VerifiedSource(bytes, true);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);
        var charged = 0L;
        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            count => { charged += count; return true; }, _ => { }, CancellationToken.None);
        Assert.Equal(block, source.ReadBytes);
        Assert.Equal(block, charged);
        var actual = new byte[block];
        Assert.Equal(block, await _store.ReadBlockAsync(identity, block, actual));
        Assert.Equal(bytes.AsSpan(block, block).ToArray(), actual);
    }

    [Fact]
    public async Task Warm_DamagedCacheCannotBypassSourceBudget()
    {
        var identity = new NativeCacheIdentity("budget", "revision", 3);
        Assert.True(await _store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        File.Delete(Path.Combine(_root, "media", "v1", identity.Key[..2], identity.Key, "content.data"));
        await using var stream = new NativeCachedStream(_store, identity,
            _ => throw new InvalidOperationException("Opened source without budget"), () => true);
        await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            _ => false, _ => { }, CancellationToken.None));
        Assert.Equal(0, await _store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task PartialWarm_DoesNotVerifyOrFetchOutsideRequestedBlocks()
    {
        var block = NativeCacheStore.BlockSize;
        var identity = new NativeCacheIdentity("partial", "revision", block + 3L);
        Assert.True(await _store.WriteBlockAsync(identity, 0, new byte[block]));
        Assert.True(await _store.WriteBlockAsync(identity, block, new byte[] { 1, 2, 3 }));
        var path = Path.Combine(_root, "media", "v1", identity.Key[..2], identity.Key, "content.data");
        await using (var file = File.OpenWrite(path)) await file.WriteAsync(new byte[] { 9 });
        await using var stream = new NativeCachedStream(_store, identity,
            _ => throw new InvalidOperationException("Opened source outside requested range"), () => true);
        await NativePrefetchExecutor.WarmAsync(_store, stream, block + 1L, 1,
            (Func<long, bool>)(_ => throw new InvalidOperationException("Spent budget for cached tail")), _ => { }, CancellationToken.None);
        Assert.Equal(identity.Length, await _store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task Warm_CachedVerificationYieldsToForegroundWithoutSpendingProviderBudget()
    {
        var identity = new NativeCacheIdentity("paused", "revision", 3);
        Assert.True(await _store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        await using var stream = new NativeCachedStream(_store, identity,
            _ => throw new InvalidOperationException("Opened source while paused"), () => true);
        await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            (Func<long, ValueTask<bool>>)(_ => throw new InvalidOperationException("Spent budget while paused")), _ => { },
            CancellationToken.None, canContinue: () => false));
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
