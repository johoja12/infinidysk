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
    public async Task SmallWarmingChunks_KeepCommittedProgressWhenLaterCapacityRunsOut()
    {
        var folder = Path.Combine(_root, "bounded");
        Directory.CreateDirectory(folder);
        await using var store = new NativeCacheStore(Path.Combine(_root, "bounded.db"),
            [new NativeCacheFolder { Id = "bounded", Path = folder, MinFreeBytes = 0,
                MaxBytes = 8L * 1024 * 1024 }]);
        var identity = new NativeCacheIdentity("chunked", "revision", 12L * 1024 * 1024);
        await using var stream = new NativeCachedStream(store, identity,
            _ => Task.FromResult<Stream>(new VerifiedSource(new byte[identity.Length], true)), () => true);

        await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(
            store, stream, 0, 0, _ => true, _ => { }, CancellationToken.None, chunkMb: 4));
        Assert.Equal(NativeCacheStore.BlockSize, await store.GetCoverageAsync(identity));
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

    [Fact]
    public async Task WholeFileWarm_PositionsSourceOncePerUncachedRun()
    {
        const int block = NativeCacheStore.BlockSize;
        var bytes = new byte[6L * block];
        new Random(6).NextBytes(bytes);
        var identity = new NativeCacheIdentity("sequential", "revision", bytes.Length);
        Assert.True(await _store.WriteBlockAsync(identity, 3L * block, bytes.AsMemory(3 * block, block)));
        var source = new VerifiedSource(bytes, true);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);

        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, _ => true, _ => { }, CancellationToken.None);

        // One pipeline per uncached run, each window spanning the run, rather than one per block.
        Assert.Equal([(0L, 3L * block), (4L * block, 2L * block)], source.Positionings);
        Assert.Equal(5L * block, source.ReadBytes);
        Assert.Equal(bytes.Length, await _store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task SharedWarmReservation_GrowsOnlyWithinFolderQuota()
    {
        var folder = Path.Combine(_root, "quota");
        Directory.CreateDirectory(folder);
        await using var store = new NativeCacheStore(Path.Combine(_root, "quota.db"),
            [new NativeCacheFolder { Id = "quota", Path = folder, MinFreeBytes = 0, MaxBytes = 9L * 1024 * 1024 }]);
        var identity = new NativeCacheIdentity("shared", "revision", 16L * 1024 * 1024);

        using var reservation = await store.ReserveWarmAsync(identity, 0);
        Assert.NotNull(reservation);
        Assert.True(await store.TryExtendWarmAsync(reservation!, NativeCacheStore.BlockSize));
        Assert.False(await store.TryExtendWarmAsync(reservation!, 2L * NativeCacheStore.BlockSize));
        // A second reservation for the same file is still refused; lanes must share the first.
        Assert.Null(await store.ReserveWarmAsync(identity, 0));
    }

    [Fact]
    public async Task LaneWarm_StalledLaneDoesNotHoldBackOtherChunks()
    {
        const int block = NativeCacheStore.BlockSize;
        var bytes = new byte[4L * block];
        new Random(4).NextBytes(bytes);
        var identity = new NativeCacheIdentity("lanes", "revision", bytes.Length);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new GatedSource(bytes, release.Task);
        await using var primary = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(stalled), () => true);
        var lanes = new WarmLanes(2, _ => Task.FromResult<NativeCachedStream?>(new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new VerifiedSource(bytes, true)), () => true)), () => true);

        var warm = NativePrefetchExecutor.WarmAsync(_store, primary, 0, 0, _ => new ValueTask<bool>(true), _ => { },
            CancellationToken.None, chunkMb: 4, lanes: lanes);

        // The primary lane holds chunk 0; the other lanes fill chunks 1–3 meanwhile.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await _store.GetCoverageAsync(identity) < 3L * block && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(3L * block, await _store.GetCoverageAsync(identity));
        Assert.False(warm.IsCompleted);

        release.SetResult();
        await warm;
        Assert.Equal(bytes.Length, await _store.GetCoverageAsync(identity));
        Assert.Equal(block, stalled.ReadBytes);
    }

    [Fact]
    public async Task LaneWarm_PrimaryFinishesAloneWhenExtraLanesCannotRun()
    {
        const int block = NativeCacheStore.BlockSize;
        var bytes = new byte[3L * block];
        new Random(3).NextBytes(bytes);
        var identity = new NativeCacheIdentity("lanes-solo", "revision", bytes.Length);
        var source = new VerifiedSource(bytes, true);
        await using var primary = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);
        var opened = 0;
        var refused = new WarmLanes(2, _ => { Interlocked.Increment(ref opened); throw new InvalidOperationException("No buffers"); }, () => true);
        var held = new WarmLanes(2, _ => { Interlocked.Increment(ref opened); return Task.FromResult<NativeCachedStream?>(null); }, () => false);

        await NativePrefetchExecutor.WarmAsync(_store, primary, 0, 2L * block, _ => new ValueTask<bool>(true), _ => { },
            CancellationToken.None, chunkMb: 4, lanes: refused);
        await NativePrefetchExecutor.WarmAsync(_store, primary, 2L * block, block, _ => new ValueTask<bool>(true), _ => { },
            CancellationToken.None, chunkMb: 4, lanes: held);

        Assert.Equal(2, opened);
        Assert.Equal(bytes.Length, source.ReadBytes);
        Assert.Equal(bytes.Length, await _store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task LaneWarm_DamageInAnExtraLaneFailsTheJobAsDamaged()
    {
        const int block = NativeCacheStore.BlockSize;
        var bytes = new byte[3L * block];
        var identity = new NativeCacheIdentity("lanes-damaged", "revision", bytes.Length);
        await using var primary = new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new VerifiedSource(bytes, true)), () => true);
        var lanes = new WarmLanes(1, _ => Task.FromResult<NativeCachedStream?>(new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new MissingArticleSource(inconclusive: false)), () => true)), () => true);

        var damaged = await Assert.ThrowsAsync<PrefetchSourceDamagedException>(() => NativePrefetchExecutor.WarmAsync(
            _store, primary, 0, 0, _ => new ValueTask<bool>(true), _ => { }, CancellationToken.None, chunkMb: 4, lanes: lanes));

        Assert.Equal("missing-segment", damaged.SegmentId);
        Assert.True(await _store.GetCoverageAsync(identity) < bytes.Length);
    }

    [Fact]
    public async Task WholeFileWarm_ReportsOnlyNewlyFilledBytesAsWarmed()
    {
        var bytes = new byte[NativeCacheStore.BlockSize + 3];
        var identity = new NativeCacheIdentity("warmed-movie", "revision", bytes.Length);
        Assert.True(await _store.WriteBlockAsync(identity, 0, bytes.AsMemory(0, NativeCacheStore.BlockSize)));
        await using var stream = new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new VerifiedSource(bytes, true)), () => true);
        var warmed = 0L;
        var lastCoverage = 0L;
        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, _ => ValueTask.FromResult(true),
            coverage => lastCoverage = coverage, CancellationToken.None, warmed: count => warmed += count);
        Assert.Equal(3, warmed); // The already cached first block is not counted.
        Assert.Equal(bytes.Length, lastCoverage);
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
        var deferred = await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0,
            _ => budget, _ => { }, CancellationToken.None));
        Assert.Equal(0, await _store.GetCoverageAsync(identity));
        if (!budget) Assert.Equal(0, source.ReadBytes);
        // Unverified source bytes are classified so repeated failures can escalate to a damaged release.
        Assert.Equal(budget ? PrefetchFailureCodes.SourceUnverified : PrefetchFailureCodes.Budget, deferred.FailureCode);
        Assert.True(deferred.CountsAsFailure == budget);
    }

    [Fact]
    public async Task ConclusiveArticleMiss_IsReportedAsSourceDamage()
    {
        var identity = new NativeCacheIdentity("damaged", "revision", 3);
        await using var stream = new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new MissingArticleSource(inconclusive: false)), () => true);
        var damaged = await Assert.ThrowsAsync<PrefetchSourceDamagedException>(() => NativePrefetchExecutor.WarmAsync(
            _store, stream, 0, 0, _ => true, _ => { }, CancellationToken.None));
        Assert.Equal("missing-segment", damaged.SegmentId);
        Assert.Contains("different post", damaged.Message);
        Assert.Equal(0, await _store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task InconclusiveArticleMiss_IsNotTreatedAsDamage()
    {
        var identity = new NativeCacheIdentity("maybe-damaged", "revision", 3);
        await using var stream = new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new MissingArticleSource(inconclusive: true)), () => true);
        var failure = await Record.ExceptionAsync(() => NativePrefetchExecutor.WarmAsync(
            _store, stream, 0, 0, _ => true, _ => { }, CancellationToken.None));
        Assert.NotNull(failure);
        Assert.IsNotType<PrefetchSourceDamagedException>(failure);
    }

    [Fact]
    public async Task ChangedSource_DefersWithoutCountingAFailure()
    {
        var identity = new NativeCacheIdentity("changed", "revision", 3);
        await using var stream = new NativeCachedStream(_store, identity,
            _ => Task.FromResult<Stream>(new VerifiedSource([1, 2, 3], true)), () => false);
        var deferred = await Assert.ThrowsAsync<PrefetchDeferredException>(() => NativePrefetchExecutor.WarmAsync(
            _store, stream, 0, 0, _ => true, _ => { }, CancellationToken.None));
        Assert.Equal(PrefetchFailureCodes.SourceChanged, deferred.FailureCode);
        Assert.False(deferred.CountsAsFailure);
    }

    [Fact]
    public async Task CachedVerification_ReportsProgressBeforeCompletion()
    {
        var identity = new NativeCacheIdentity("verified-movie", "revision", 2L * NativeCacheStore.BlockSize);
        var bytes = new byte[identity.Length];
        Assert.True(await _store.WriteBlockAsync(identity, 0, bytes.AsMemory(0, NativeCacheStore.BlockSize)));
        Assert.True(await _store.WriteBlockAsync(identity, NativeCacheStore.BlockSize, bytes.AsMemory(NativeCacheStore.BlockSize)));
        await using var stream = new NativeCachedStream(_store, identity, _ => throw new InvalidOperationException("Opened source"), () => true);
        var reports = new List<long>();

        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, (Func<long, bool>)(_ => throw new InvalidOperationException("Spent budget")),
            reports.Add, CancellationToken.None);

        Assert.Equal(NativeCacheStore.BlockSize, reports[0]); // Movement is visible before the whole pass completes.
        Assert.Equal(identity.Length, reports[^1]);
    }

    [Fact]
    public async Task RecentlyWarm_RequiresRecordedVerificationOfCurrentGenerationAndCompleteCatalogue()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var current = new NativeCacheIdentity(item.ToString("N"), "rev-a", NativeCacheStore.BlockSize + 3L);
        var bytes = new byte[current.Length];
        Assert.True(await _store.WriteBlockAsync(current, 0, bytes.AsMemory(0, NativeCacheStore.BlockSize)));
        (long, long)[] whole = [(0, 0)];

        jobs.RecordVerified(item, "rev-a", 0, 0);
        Assert.False(await NativePrefetchExecutor.IsRecentlyWarmAsync(_store, jobs, current, item, whole, since, CancellationToken.None)); // Tail block missing.
        Assert.True(await NativePrefetchExecutor.IsRecentlyWarmAsync(_store, jobs, current, item, [(0, 16)], since, CancellationToken.None));

        Assert.True(await _store.WriteBlockAsync(current, NativeCacheStore.BlockSize, bytes.AsMemory(NativeCacheStore.BlockSize)));
        Assert.True(await NativePrefetchExecutor.IsRecentlyWarmAsync(_store, jobs, current, item, whole, since, CancellationToken.None));

        var changed = current with { Generation = "rev-b" };
        Assert.False(await NativePrefetchExecutor.IsRecentlyWarmAsync(_store, jobs, changed, item, whole, since, CancellationToken.None));
        Assert.False(await NativePrefetchExecutor.IsRecentlyWarmAsync(_store, jobs, current, item, whole,
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None));
        Assert.False(await NativePrefetchExecutor.IsRecentlyWarmAsync(_store, jobs, current, item, [], since, CancellationToken.None));
    }

    [Fact]
    public void VerificationSample_AlwaysIncludesHeadAndTailAndBoundsTheStridedSample()
    {
        var sample = new VerificationSample(0);
        const long blocks = 3000; // About 12 GB of 4 MiB blocks.
        var included = Enumerable.Range(0, (int)blocks).Where(index => sample.Includes(index, blocks)).ToList();

        Assert.All(Enumerable.Range(0, VerificationSample.HeadBlocks), index => Assert.Contains(index, included));
        Assert.Contains((int)blocks - 1, included);
        Assert.Contains((int)blocks - VerificationSample.TailBlocks, included);
        Assert.InRange(included.Count, VerificationSample.MinStridedBlocks,
            VerificationSample.HeadBlocks + VerificationSample.TailBlocks + VerificationSample.MaxStridedBlocks);
        const long huge = 100_000; // About 400 GB: the strided sample stays capped.
        Assert.InRange(Enumerable.Range(0, (int)huge).LongCount(index => sample.Includes(index, huge)),
            VerificationSample.MaxStridedBlocks, VerificationSample.HeadBlocks + VerificationSample.TailBlocks + VerificationSample.MaxStridedBlocks);
        Assert.False(sample.Includes(-1, blocks));
        Assert.False(sample.Includes(blocks, blocks));
    }

    [Fact]
    public void VerificationSample_RotatesBetweenRunsButIsStableWithinARun()
    {
        const long blocks = 3000;
        static HashSet<long> Sampled(VerificationSample sample) =>
            Enumerable.Range(0, (int)blocks).Select(index => (long)index).Where(index => sample.Includes(index, blocks)).ToHashSet();

        var today = VerificationSample.ForRun(new DateTimeOffset(2026, 9, 30, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal(today, VerificationSample.ForRun(new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero)));
        var tomorrow = VerificationSample.ForRun(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
        Assert.NotEqual(Sampled(today), Sampled(tomorrow));
        Assert.Equal(Sampled(today), Sampled(today));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutineRecheck_SkipsUnsampledBlocksWhileFullRecheckFindsTheirDamage(bool full)
    {
        // 24 blocks with seed 0: head 0-3, tail 22-23 and strided 0-15 are hashed; 16-21 are skipped.
        var (identity, bytes) = await WriteBlocksAsync("sampled-movie", 24);
        await CorruptBlockAsync(identity, 18);
        var source = new VerifiedSource(bytes, true);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);

        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, _ => new ValueTask<bool>(true), _ => { }, CancellationToken.None,
            sampling: full ? null : new VerificationSample(0));

        // Routine sampling never opens the source for an unsampled block; a full pass refills it.
        Assert.Equal(full ? NativeCacheStore.BlockSize : 0, source.ReadBytes);
    }

    [Fact]
    public async Task RoutineRecheck_DamagedSampledBlockEscalatesToAFullPass()
    {
        var (identity, bytes) = await WriteBlocksAsync("escalated-movie", 24);
        await CorruptBlockAsync(identity, 10); // Sampled.
        await CorruptBlockAsync(identity, 18); // Unsampled; only found by escalating.
        var source = new VerifiedSource(bytes, true);
        await using var stream = new NativeCachedStream(_store, identity, _ => Task.FromResult<Stream>(source), () => true);
        var reports = new List<long>();

        await NativePrefetchExecutor.WarmAsync(_store, stream, 0, 0, _ => new ValueTask<bool>(true), reports.Add, CancellationToken.None,
            sampling: new VerificationSample(0));

        Assert.Equal(2L * NativeCacheStore.BlockSize, source.ReadBytes);
        foreach (var index in new[] { 10, 18 })
        {
            var actual = new byte[NativeCacheStore.BlockSize];
            Assert.Equal(actual.Length, await _store.ReadBlockAsync(identity, (long)index * NativeCacheStore.BlockSize, actual));
            Assert.Equal(bytes.AsSpan(index * NativeCacheStore.BlockSize, NativeCacheStore.BlockSize).ToArray(), actual);
        }
        Assert.Equal(identity.Length, reports[^1]);
    }

    private async Task<(NativeCacheIdentity Identity, byte[] Bytes)> WriteBlocksAsync(string name, int blocks)
    {
        var bytes = new byte[(long)blocks * NativeCacheStore.BlockSize];
        new Random(blocks).NextBytes(bytes);
        var identity = new NativeCacheIdentity(name, "revision", bytes.Length);
        for (var index = 0; index < blocks; index++)
            Assert.True(await _store.WriteBlockAsync(identity, (long)index * NativeCacheStore.BlockSize,
                bytes.AsMemory(index * NativeCacheStore.BlockSize, NativeCacheStore.BlockSize)));
        return (identity, bytes);
    }

    private async Task CorruptBlockAsync(NativeCacheIdentity identity, int index)
    {
        var path = Path.Combine(_root, "media", "v1", identity.Key[..2], identity.Key, "content.data");
        await using var file = File.OpenWrite(path);
        file.Position = (long)index * NativeCacheStore.BlockSize;
        await file.WriteAsync(new byte[NativeCacheStore.BlockSize]);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private sealed class MissingArticleSource(bool inconclusive) : MemoryStream(new byte[3]), ICacheReadEvidence
    {
        public bool LastReadCacheable => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new NzbWebDAV.Exceptions.UsenetArticleNotFoundException("missing-segment")
            { InconclusiveReason = inconclusive ? "A provider timed out." : null };
    }

    private sealed class GatedSource(byte[] bytes, Task gate) : MemoryStream(bytes), ICacheReadEvidence
    {
        public long ReadBytes { get; private set; }
        public bool LastReadCacheable => true;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            var read = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += read;
            return read;
        }
    }

    private sealed class VerifiedSource(byte[] bytes, bool verified) : MemoryStream(bytes), ICacheReadEvidence
    {
        public long ReadBytes { get; private set; }
        /// <summary>Each repositioning with the native read window in force when it happened.</summary>
        public List<(long Offset, long Window)> Positionings { get; } = [];
        public bool LastReadCacheable => verified;
        public override long Position
        {
            get => base.Position;
            set
            {
                if (NativeCacheReadContext.ReadBudget is { } window) Positionings.Add((value, window));
                base.Position = value;
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += read;
            return read;
        }
    }
}
