using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Tests.Streams;

public sealed class NativeCachedStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-stream-tests-" + Guid.NewGuid().ToString("N"));
    public NativeCachedStreamTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ConcurrentVerification_SharesOnlyInFlightEvidence_AndRechecksLaterDamage()
    {
        await using var store = CreateStore();
        var identity = new NativeCacheIdentity("shared-verification", "v1", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var first = new NativeCachedStream(store, identity, _ => throw new Exception("source"), () => true)
        {
            BeforeCacheIo = async (_, _) => { Interlocked.Increment(ref reads); entered.TrySetResult(); await release.Task; }
        };
        await using var second = new NativeCachedStream(store, identity, _ => throw new Exception("source"), () => true)
        {
            BeforeCacheIo = (_, _) => { Interlocked.Increment(ref reads); return Task.CompletedTask; }
        };
        var owner = first.VerifyCachedBlockAsync(0, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var follower = second.VerifyCachedBlockAsync(0, CancellationToken.None);
        release.TrySetResult();
        Assert.True(await owner);
        Assert.True(await follower);
        Assert.Equal(1, reads);
        Assert.True(await second.VerifyCachedBlockAsync(0, CancellationToken.None));
        Assert.Equal(2, reads); // No TTL-based trust of old catalogue or verification results.
        await File.WriteAllBytesAsync(Path.Combine(_root, "v1", identity.Key[..2], identity.Key, "content.data"), new byte[] { 9, 9, 9 });
        Assert.False(await second.VerifyCachedBlockAsync(0, CancellationToken.None));
        Assert.Equal(0, await store.GetCoverageAsync(identity));
    }

    [Fact]
    public async Task WriteBehind_ServesBytesBeforeFlush_AndRetainsBufferUntilPublication()
    {
        await using var store = CreateStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TrackingAdmission();
        var identity = new NativeCacheIdentity("write-behind", "v1", 3);
        await using var stream = new NativeCachedStream(store, identity,
            _ => Task.FromResult<Stream>(new EvidenceStream(true)), () => true, admission, writeBehind: true)
        {
            CacheIoTimeout = TimeSpan.FromMilliseconds(30),
            BeforeCacheIo = async (write, _) => { if (write) { entered.TrySetResult(); await release.Task; } }
        };
        try
        {
            var bytes = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, await store.GetCoverageAsync(identity));
            await stream.DisposeAsync();
            Assert.False(admission.Disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await admission.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, await store.GetCoverageAsync(identity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledCacheIo_FallsBackButRetainsBufferAdmissionUntilIoEnds(bool write)
    {
        await using var store = CreateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TrackingAdmission();
        await using var stream = new NativeCachedStream(store, new("slow", "v1", 3),
            _ => Task.FromResult<Stream>(new EvidenceStream(true)), () => true, admission)
        {
            CacheIoTimeout = TimeSpan.FromMilliseconds(50),
            BeforeCacheIo = async (isWrite, _) =>
            {
                if (isWrite != write) return;
                entered.TrySetResult();
                await release.Task;
            }
        };
        var bytes = new byte[3];
        var read = stream.ReadAsync(bytes).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            Assert.False(stream.LastReadCacheable);
            await stream.DisposeAsync();
            Assert.False(admission.Disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await admission.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TrackingAdmission : IDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledBackgroundIo_RetainsAdmissionUntilUncancellableIoCompletes(bool verifyOnly)
    {
        await using var store = CreateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TrackingAdmission();
        using var cancellation = new CancellationTokenSource();
        await using var stream = new NativeCachedStream(store, new("cancel", "v1", 3),
            _ => throw new InvalidOperationException("Cancelled warming opened source"), () => true, admission, background: true)
        {
            BeforeCacheIo = async (_, _) => { entered.TrySetResult(); await release.Task; }
        };
        Task read = verifyOnly ? stream.VerifyCachedBlockAsync(0, cancellation.Token)
            : stream.ReadAsync(new byte[3], cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            await stream.DisposeAsync();
            Assert.False(admission.Disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await admission.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StalledFillOwner_DoesNotBlockAnotherForegroundReader()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("fill", "v1", 3);
        using var held = await store.AcquireFillAsync(id, 0, CancellationToken.None);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new EvidenceStream(true)), () => true)
        { CacheIoTimeout = TimeSpan.FromMilliseconds(50) };
        Assert.Equal(3, await stream.ReadAsync(new byte[3]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(stream.LastReadCacheable);
    }

    [Fact]
    public async Task VerifiedFill_SubsequentReadNeedsNoSourceOpen()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await using (var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(new EvidenceStream(true)), () => true))
        {
            var bytes = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(bytes));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        }
        await using var hit = new NativeCachedStream(store, id, _ => throw new InvalidOperationException("Cache hit opened source"), () => true);
        Assert.Equal(3, await hit.ReadAsync(new byte[3]));
    }

    [Fact]
    public async Task ColdAlignedRead_ReturnsPrefixBeforeTheRestOfTheBlock_ThenPublishesVerifiedFill()
    {
        await using var store = CreateStore();
        var bytes = new byte[NativeCacheStore.BlockSize];
        new Random(81).NextBytes(bytes);
        var reads = new List<(long Offset, int Count)>();
        var id = new NativeCacheIdentity("incremental", "v1", bytes.Length);
        await using (var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new TrackedSource(bytes, reads)), () => true))
        {
            var first = new byte[NativeCacheStore.BlockSize];
            var firstCount = await stream.ReadAsync(first);
            Assert.Equal(64 * 1024, firstCount);
            Assert.Equal(bytes.AsSpan(0, firstCount).ToArray(), first.AsSpan(0, firstCount).ToArray());
            Assert.Equal([(0, firstCount)], reads);
            Assert.Equal(0, await store.GetCoverageAsync(id));
            var second = new byte[64 * 1024];
            Assert.Equal(second.Length, await stream.ReadAsync(second));
            Assert.Equal(bytes.AsSpan(firstCount, second.Length).ToArray(), second);
            Assert.Equal([(0, firstCount), (firstCount, bytes.Length - firstCount)], reads);
            Assert.Equal(bytes.Length, await store.GetCoverageAsync(id));
            var rest = new byte[bytes.Length - firstCount - second.Length];
            await stream.ReadExactlyAsync(rest);
            Assert.Equal(bytes.AsSpan(firstCount + second.Length).ToArray(), rest);
        }
        Assert.Equal(bytes.Length, await store.GetCoverageAsync(id));
    }

    [Fact]
    public async Task WarmProbe_FillsAndCommitsWholeBlockBeforeReturningOneByte()
    {
        await using var store = CreateStore();
        var bytes = new byte[NativeCacheStore.BlockSize];
        new Random(84).NextBytes(bytes);
        var reads = new List<(long Offset, int Count)>();
        var id = new NativeCacheIdentity("warm-probe", "v1", bytes.Length);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new TrackedSource(bytes, reads)), () => true);
        var probe = new byte[1];

        Assert.Equal(1, await stream.ReadWarmProbeAsync(probe, CancellationToken.None));
        Assert.Equal(bytes[0], probe[0]);
        Assert.Equal([(0, NativeCacheStore.BlockSize)], reads);
        Assert.Equal(bytes.Length, await store.GetCoverageAsync(id));
    }

    [Fact]
    public async Task ColdUnalignedSeek_ReadsRequestedOffsetWithoutFetchingEarlierBytes()
    {
        await using var store = CreateStore();
        var bytes = new byte[NativeCacheStore.BlockSize + 37];
        new Random(82).NextBytes(bytes);
        var reads = new List<(long Offset, int Count)>();
        var id = new NativeCacheIdentity("seek", "v1", bytes.Length);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new TrackedSource(bytes, reads)), () => true);
        stream.Position = NativeCacheStore.BlockSize / 2;
        var first = new byte[64 * 1024];
        Assert.Equal(first.Length, await stream.ReadAsync(first));
        Assert.Equal(bytes.AsSpan((int)stream.Position - first.Length, first.Length).ToArray(), first);
        Assert.Equal([(NativeCacheStore.BlockSize / 2, first.Length)], reads);
        Assert.Equal(0, await store.GetCoverageAsync(id));
    }

    [Fact]
    public async Task VerifiedFill_KeepsProofContextAcrossBlockBoundary()
    {
        await using var store = CreateStore();
        var bytes = new byte[NativeCacheStore.BlockSize + 7];
        new Random(83).NextBytes(bytes);
        var id = new NativeCacheIdentity("proof-context", "v1", bytes.Length);
        var source = new ContextAwareSource(bytes);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(source), () => true);
        var actual = new byte[bytes.Length];
        await stream.ReadExactlyAsync(actual);
        Assert.Equal(bytes, actual);
        Assert.Equal(bytes.Length, await store.GetCoverageAsync(id));
        Assert.All(source.PositionsInProofContext, Assert.True);
        Assert.All(source.ReadsInProofContext, Assert.True);
    }

    [Fact]
    public async Task SequentialColdBlocks_ReuseBoundedSourceWindow_AndPublishEachBlock()
    {
        await using var store = CreateStore();
        var block = NativeCacheStore.BlockSize;
        var bytes = new byte[5 * block];
        new Random(85).NextBytes(bytes);
        var source = new WindowedProofSource(bytes);
        var id = new NativeCacheIdentity("sequential-window", "v1", bytes.Length);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(source), () => true);
        var actual = new byte[block];
        for (var i = 0; i < 5; i++)
        {
            await stream.ReadExactlyAsync(actual);
            Assert.Equal(bytes.AsSpan(i * block, block).ToArray(), actual);
            Assert.Equal((i + 1L) * block, await store.GetCoverageAsync(id));
        }
        Assert.Equal([(0L, 4L * block), (4L * block, (long)block)], source.Windows);
    }

    [Fact]
    public async Task FiniteRange_ReadAheadStopsAtFinalIntegrityBlock()
    {
        await using var store = CreateStore();
        var block = NativeCacheStore.BlockSize;
        var source = new WindowedProofSource(new byte[8 * block]);
        var id = new NativeCacheIdentity("finite-window", "v1", source.Length);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(source), () => true);
        stream.Position = block;
        RangeContext.SetReadBudget(4L * block + 1);
        try
        {
            await stream.ReadExactlyAsync(new byte[4 * block + 1]);
            Assert.Equal([(1L * block, 4L * block), (5L * block, (long)block)], source.Windows);
            Assert.Equal(5L * block, await store.GetCoverageAsync(id));
        }
        finally { RangeContext.SetReadBudget(null); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Warming_DoesNotPrefetchBeyondTheAdmittedBlock(bool background)
    {
        await using var store = CreateStore();
        var block = NativeCacheStore.BlockSize;
        var source = new WindowedProofSource(new byte[5 * block]);
        await using var stream = new NativeCachedStream(store, new("warming-window", "v1", source.Length),
            _ => Task.FromResult<Stream>(source), () => true, background: background);
        if (background) await stream.ReadExactlyAsync(new byte[2 * block]);
        else
        {
            await stream.ReadWarmProbeAsync(new byte[1], CancellationToken.None);
            stream.Position = block;
            await stream.ReadWarmProbeAsync(new byte[1], CancellationToken.None);
        }
        Assert.Equal([(0L, (long)block), ((long)block, (long)block)], source.Windows);
    }

    [Fact]
    public async Task CacheHitGap_RepositionsSourceRatherThanServingSkippedBytes()
    {
        await using var store = CreateStore();
        var block = NativeCacheStore.BlockSize;
        var bytes = new byte[3 * block];
        new Random(86).NextBytes(bytes);
        var source = new WindowedProofSource(bytes);
        var id = new NativeCacheIdentity("gap-window", "v1", bytes.Length);
        Assert.True(await store.WriteBlockAsync(id, block, bytes.AsMemory(block, block)));
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(source), () => true);
        var actual = new byte[bytes.Length];
        await stream.ReadExactlyAsync(actual);
        Assert.Equal(bytes, actual);
        Assert.Equal([(0L, 3L * block), (2L * block, (long)block)], source.Windows);
    }

    [Fact]
    public async Task UnalignedPrefix_EntersProofWindowAtNextBlock()
    {
        await using var store = CreateStore();
        var block = NativeCacheStore.BlockSize;
        var bytes = new byte[3 * block];
        new Random(87).NextBytes(bytes);
        var source = new ContextAwareSource(bytes);
        var id = new NativeCacheIdentity("unaligned-window", "v1", bytes.Length);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(source), () => true);
        stream.Position = block - 64 * 1024;
        var actual = new byte[2 * block + 64 * 1024];
        await stream.ReadExactlyAsync(actual);
        Assert.Equal(bytes.AsSpan(block - 64 * 1024).ToArray(), actual);
        Assert.Equal(2L * block, await store.GetCoverageAsync(id));
        Assert.Equal([false, true], source.PositionsInProofContext);
        Assert.False(source.ReadsInProofContext[0]);
        Assert.All(source.ReadsInProofContext.Skip(1), Assert.True);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnverifiedOrStaleSource_IsReturnedButNotCached(bool evidence, bool current)
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await using var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(new EvidenceStream(evidence)), () => current);
        Assert.Equal(3, await stream.ReadAsync(new byte[3]));
        Assert.Equal(0, await store.GetCoverageAsync(id));
    }

    private NativeCacheStore CreateStore() => new(Path.Combine(_root, "index.db"),
        [new NativeCacheFolder { Path = _root, MinFreeBytes = 0 }]);

    [Fact]
    public async Task GenerationChanges_FailResponseRatherThanMixOldAndNewBytes()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await store.WriteBlockAsync(id, 0, new byte[] { 1, 2, 3 });
        var current = true;
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[] { 9, 9, 9 })), () => current);
        var bytes = new byte[1];
        await stream.ReadAsync(bytes);
        Assert.Equal(1, bytes[0]);
        current = false;
        await Assert.ThrowsAsync<MediaSourceChangedException>(() => stream.ReadAsync(bytes).AsTask());
    }

    [Fact]
    public async Task SequentialReads_GrowSourceWindow_AndSeekResetsIt()
    {
        await using var store = CreateStore();
        const long mib = 1024 * 1024;
        var id = new NativeCacheIdentity("id", "version", 300 * mib);
        var source = new BudgetRecordingStream(300 * mib);
        await using var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(source), () => true);
        var buffer = new byte[mib];
        for (var read = 0L; read < 120 * mib;)
            read += await stream.ReadAsync(buffer);
        Assert.Equal(new long?[] { 16 * mib, 32 * mib, 64 * mib, 128 * mib }, source.WindowBudgets);

        stream.Position = 40 * mib; // a seek starts small again
        await stream.ReadAsync(buffer);
        Assert.Equal(16 * mib, source.WindowBudgets[^1]);
    }

    /// <summary>Zero-filled source that records the native read budget each time it is positioned.</summary>
    private sealed class BudgetRecordingStream(long length) : Stream
    {
        private long _position;
        public List<long?> WindowBudgets { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set { WindowBudgets.Add(NativeCacheReadContext.ReadBudget); _position = value; }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SpeculativeTailFailure_DoesNotBreakReadablePrefix()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new FailingTailStream()), () => true);
        var bytes = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(bytes));
        Assert.Equal(1, bytes[0]);
        Assert.Equal(0, await store.GetCoverageAsync(id));
    }

    [Fact]
    public async Task ConcurrentMisses_CoalesceOneVerifiedFill()
    {
        await using var store = CreateStore();
        var identity = new NativeCacheIdentity("item", "v1", 3);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        async Task<Stream> Open(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            opened.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new EvidenceStream(true);
        }
        await using var first = new NativeCachedStream(store, identity, Open, () => true);
        await using var second = new NativeCachedStream(store, identity, Open, () => true);
        var firstRead = first.ReadAsync(new byte[3]).AsTask();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRead = second.ReadAsync(new byte[3]).AsTask();
        release.SetResult();
        await Task.WhenAll(firstRead, secondRead).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, opens);
    }

    [Fact]
    public void MetadataPath_RejectsSymlinkAncestors()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        Assert.Throws<ArgumentException>(() => NativeFileSystem.RequireLocalMetadata(Path.Combine(link, "index.db")));
    }

    [Fact]
    public async Task UnalignedRange_CrossesCachedHeadMissingMiddleAndCachedTailWithoutChangingBytes()
    {
        await using var store = CreateStore();
        var block = NativeCacheStore.BlockSize;
        var expected = new byte[block * 2 + 37];
        new Random(487).NextBytes(expected);
        var identity = new NativeCacheIdentity("gapped", "revision", expected.Length);
        Assert.True(await store.WriteBlockAsync(identity, 0, expected.AsMemory(0, block)));
        Assert.True(await store.WriteBlockAsync(identity, block * 2L, expected.AsMemory(block * 2)));
        var reads = new List<(long Offset, int Count)>();
        await using (var stream = new NativeCachedStream(store, identity,
            _ => Task.FromResult<Stream>(new TrackedSource(expected, reads)), () => true))
        {
            stream.Position = block - 17;
            var actual = new byte[block + 46];
            await stream.ReadExactlyAsync(actual);
            Assert.Equal(expected.AsSpan(block - 17, actual.Length).ToArray(), actual);
            Assert.Equal([(block, block)], reads);
        }

        await using var cached = new NativeCachedStream(store, identity,
            _ => throw new InvalidOperationException("Filled gap was fetched again"), () => true);
        var all = new byte[expected.Length];
        await cached.ReadExactlyAsync(all);
        Assert.Equal(expected, all);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class EvidenceStream(bool verified) : MemoryStream(new byte[] { 1, 2, 3 }), ICacheReadEvidence
    {
        public bool LastReadCacheable => verified;
    }

    private sealed class TrackedSource(byte[] bytes, List<(long Offset, int Count)> reads) : MemoryStream(bytes), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var offset = Position;
            var count = await base.ReadAsync(buffer, cancellationToken);
            reads.Add((offset, count));
            return count;
        }
    }

    private sealed class WindowedProofSource(byte[] bytes) : MemoryStream(bytes), ICacheReadEvidence
    {
        private long _windowEnd;
        public bool LastReadCacheable => true;
        public List<(long Offset, long Budget)> Windows { get; } = [];
        public override long Position
        {
            get => base.Position;
            set
            {
                var budget = NativeCacheReadContext.ReadBudget
                    ?? throw new InvalidOperationException("Source positioned outside proof context.");
                Windows.Add((value, budget));
                _windowEnd = value + budget;
                base.Position = value;
            }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.True(NativeCacheReadContext.IsActive);
            if (Position >= _windowEnd) throw new IOException("Finite source window exhausted.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _windowEnd - Position)], cancellationToken);
        }
    }

    private sealed class ContextAwareSource(byte[] bytes) : MemoryStream(bytes), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
        public List<bool> PositionsInProofContext { get; } = [];
        public List<bool> ReadsInProofContext { get; } = [];
        public override long Position
        {
            get => base.Position;
            set
            {
                PositionsInProofContext.Add(NativeCacheReadContext.IsActive);
                base.Position = value;
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadsInProofContext.Add(NativeCacheReadContext.IsActive);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingTailStream() : MemoryStream(new byte[] { 1, 2, 3 }), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new IOException("Later article unavailable");
            return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}
