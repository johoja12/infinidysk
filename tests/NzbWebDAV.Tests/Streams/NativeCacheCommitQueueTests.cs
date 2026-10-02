using System.Diagnostics;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class NativeCacheCommitQueueTests : IDisposable
{
    private const int Block = NativeCacheStore.BlockSize;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-commit-queue-" + Guid.NewGuid().ToString("N"));

    public NativeCacheCommitQueueTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Queue_PublishesAPrivateCopy()
    {
        await using var store = CreateStore();
        await using var queue = new NativeCacheCommitQueue();
        var identity = new NativeCacheIdentity("copy", "v1", 3);
        var block = new byte[] { 1, 2, 3 };
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(queue.TryEnqueue(store, identity, 0, block, done.SetResult));
        block[0] = block[1] = block[2] = 0; // the caller may reuse its buffer immediately
        Assert.True(await done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var read = new byte[3];
        Assert.Equal(3, await store.ReadBlockAsync(identity, 0, read));
        Assert.Equal(new byte[] { 1, 2, 3 }, read);
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public async Task Queue_CommitsBlocksOfOneFileInParallel()
    {
        // Every commit waits until four are in flight at once: serial commits would never get there.
        var inFlight = 0;
        var allInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = CreateStore(async token =>
        {
            if (Interlocked.Increment(ref inFlight) >= NativeCacheCommitQueue.DefaultWorkers) allInFlight.TrySetResult();
            await allInFlight.Task.WaitAsync(token);
        });
        var identity = new NativeCacheIdentity("parallel", "v1", 9L * Block);
        var data = Pattern(9 * Block);
        allInFlight.TrySetResult();
        Assert.True(await store.WriteBlockAsync(identity, 0, data.AsMemory(0, Block)));
        inFlight = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        allInFlight = gate;
        await using var queue = new NativeCacheCommitQueue();
        var done = Enumerable.Range(1, 8).Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var index = 1; index <= 8; index++)
            Assert.True(queue.TryEnqueue(store, identity, (long)index * Block, data.AsSpan(index * Block, Block), done[index - 1].SetResult));
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(await Task.WhenAll(done.Select(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.True(result));
        Assert.Equal(identity.Length, await store.GetCoverageAsync(identity));
        var read = new byte[Block];
        for (var index = 0; index < 9; index++)
        {
            Assert.Equal(Block, await store.ReadBlockAsync(identity, (long)index * Block, read));
            Assert.True(read.AsSpan().SequenceEqual(data.AsSpan(index * Block, Block)));
        }
    }

    [Fact]
    public async Task Queue_RefusesWorkBeyondItsByteBudget()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = CreateStore(_ => release.Task);
        await using var queue = new NativeCacheCommitQueue(capacityBytes: Block, workers: 1);
        var identity = new NativeCacheIdentity("budget", "v1", 2L * Block);
        var block = new byte[Block];
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(queue.TryEnqueue(store, identity, 0, block, first.SetResult));
        Assert.False(queue.TryEnqueue(store, identity, Block, block, _ => { }));
        release.SetResult();
        Assert.True(await first.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(queue.TryEnqueue(store, identity, Block, block, _ => { }));
    }

    [Fact]
    public async Task Queue_ReportsUnpublishedBlocksWhenStopped()
    {
        await using var store = CreateStore(token => Task.Delay(Timeout.Infinite, token));
        var queue = new NativeCacheCommitQueue(workers: 1);
        var identity = new NativeCacheIdentity("shutdown", "v1", 2L * Block);
        var results = new List<bool>();
        var block = new byte[Block];
        Assert.True(queue.TryEnqueue(store, identity, 0, block, committed => { lock (results) results.Add(committed); }));
        Assert.True(queue.TryEnqueue(store, identity, Block, block, committed => { lock (results) results.Add(committed); }));
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        lock (results) Assert.Equal(new[] { false, false }, results);
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public async Task SlowCommits_DoNotBlockPlayback_AndTheCacheStillCompletes()
    {
        await using var store = CreateStore(token => Task.Delay(500, token));
        var queue = new NativeCacheCommitQueue();
        var data = Pattern(3 * Block);
        var identity = new NativeCacheIdentity("slow-nas", "v1", data.Length);
        var backfills = new List<(long, long)>();
        var watch = Stopwatch.StartNew();
        await using (var stream = new NativeCachedStream(store, identity, _ => Task.FromResult<Stream>(new CacheableSource(data)),
            () => true, writeBehind: true, commitQueue: queue, backfill: (start, length) => backfills.Add((start, length)))
            { CacheIoTimeout = TimeSpan.FromMilliseconds(50) })
        {
            var actual = new byte[data.Length];
            await stream.ReadExactlyAsync(actual);
            Assert.Equal(data, actual);
        }
        // Three 500 ms commits would have cost the old path a timeout and the rest of the response.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.2), $"playback waited {watch.Elapsed}");
        await queue.DisposeAsync();
        Assert.Equal(data.Length, await store.GetCoverageAsync(identity));
        Assert.Empty(backfills);
    }

    [Fact]
    public async Task FullQueue_SchedulesOneMergedBackfillForPlayback()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = CreateStore(_ => release.Task);
        await using var queue = new NativeCacheCommitQueue(capacityBytes: Block, workers: 1);
        var data = Pattern(6 * Block);
        var identity = new NativeCacheIdentity("full-queue", "v1", data.Length);
        var backfills = new List<(long Start, long Length)>();
        await using (var stream = new NativeCachedStream(store, identity, _ => Task.FromResult<Stream>(new CacheableSource(data)),
            () => true, writeBehind: true, commitQueue: queue, backfill: (start, length) => backfills.Add((start, length))))
        {
            var actual = new byte[data.Length];
            await stream.ReadExactlyAsync(actual);
            Assert.Equal(data, actual);
        }
        release.SetResult();
        // Block 0 went to the queue; blocks 1..5 found it full and become one backfill range.
        Assert.Equal(new[] { ((long)Block, 5L * Block) }, backfills);
    }

    [Fact]
    public async Task ShortReads_NeverScheduleBackfill()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = CreateStore(_ => release.Task);
        await using var queue = new NativeCacheCommitQueue(capacityBytes: Block, workers: 1);
        var data = Pattern(6 * Block);
        var identity = new NativeCacheIdentity("probe", "v1", data.Length);
        var backfills = new List<(long, long)>();
        await using (var stream = new NativeCachedStream(store, identity, _ => Task.FromResult<Stream>(new CacheableSource(data)),
            () => true, writeBehind: true, commitQueue: queue, backfill: (start, length) => backfills.Add((start, length))))
        {
            var probe = new byte[2 * Block];
            await stream.ReadExactlyAsync(probe);
        }
        release.SetResult();
        Assert.Empty(backfills);
    }

    private NativeCacheStore CreateStore(Func<CancellationToken, Task>? beforeWrite = null) =>
        new(Path.Combine(_root, "index.db"), [new NativeCacheFolder { Path = _root, MinFreeBytes = 0 }])
        {
            BeforeWriteReserveAsync = beforeWrite,
        };

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(index => (byte)(index * 13)).ToArray();

    private sealed class CacheableSource(byte[] data) : MemoryStream(data), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
