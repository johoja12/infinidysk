using System.Diagnostics;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

/// <summary>
/// Native Cache buffer slots are held per block, not per response (#118): finished blocks return
/// their slot, idle readers give theirs up, and a slow probe or busy fill costs one block only.
/// </summary>
public sealed class NativeBufferAdmissionTests : IDisposable
{
    private const int Block = NativeCacheStore.BlockSize;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-admission-" + Guid.NewGuid().ToString("N"));

    public NativeBufferAdmissionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadyBlock_ReturnsItsSlotWhileTheResponseStaysOpen()
    {
        await using var store = CreateStore();
        var slots = new NativeBufferSlots(1);
        var first = Pattern(2 * Block, 1);
        var second = Pattern(2 * Block, 2);
        var firstId = new NativeCacheIdentity("first", "v1", first.Length);
        var secondId = new NativeCacheIdentity("second", "v1", second.Length);
        await SeedLastBlockAsync(store, firstId, first);
        await SeedLastBlockAsync(store, secondId, second);
        await using (var queue = new NativeCacheCommitQueue())
        {
            await using var open = Stream(store, firstId, first, slots, queue);
            var actual = new byte[first.Length];
            var half = Block / 2;
            await open.ReadExactlyAsync(actual.AsMemory(0, half));
            // The block is filled and handed to its commit: the reader keeps only the block it is
            // serving, and the slot is free while the rest of the block plays out.
            Assert.Equal(0, slots.Held);
            Assert.Equal(1, slots.Serving);
            await open.ReadExactlyAsync(actual.AsMemory(half, Block - half));
            Assert.Equal(0, slots.Serving);

            // The only slot is free between and within the open response's blocks, so another reader caches.
            await using (var other = Stream(store, secondId, second, slots, queue, admissionWait: TimeSpan.Zero))
            {
                var otherBytes = new byte[second.Length];
                await other.ReadExactlyAsync(otherBytes);
                Assert.Equal(second, otherBytes);
                Assert.True(other.LastReadCacheable);
            }

            await open.ReadExactlyAsync(actual.AsMemory(Block));
            Assert.Equal(first, actual);
        }
        Assert.Equal(first.Length, await store.GetCoverageAsync(firstId));
        Assert.Equal(second.Length, await store.GetCoverageAsync(secondId));
        Assert.Equal(0, slots.Held);
    }

    [Fact]
    public async Task MoreConcurrentReadersThanSlots_AllCacheEveryBlock()
    {
        await using var store = CreateStore();
        var slots = new NativeBufferSlots(2);
        var backfills = 0L;
        const int readers = 6;
        var data = Enumerable.Range(0, readers).Select(index => Pattern(3 * Block, index)).ToArray();
        var ids = Enumerable.Range(0, readers).Select(index => new NativeCacheIdentity($"reader-{index}", "v1", data[index].Length)).ToArray();
        for (var index = 0; index < readers; index++) await SeedLastBlockAsync(store, ids[index], data[index]);
        var watch = Stopwatch.StartNew();
        await using (var queue = new NativeCacheCommitQueue())
        {
            await Task.WhenAll(Enumerable.Range(0, readers).Select(index => Task.Run(async () =>
            {
                await using var stream = Stream(store, ids[index], data[index], slots, queue,
                    backfill: (_, length) => Interlocked.Add(ref backfills, length));
                var actual = new byte[data[index].Length];
                for (var offset = 0; offset < actual.Length;)
                {
                    offset += await stream.ReadAsync(actual.AsMemory(offset, Math.Min(256 * 1024, actual.Length - offset)));
                    await Task.Yield();
                }
                Assert.Equal(data[index], actual);
            })));
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"readers took {watch.Elapsed}");
        for (var index = 0; index < readers; index++)
            Assert.Equal(data[index].Length, await store.GetCoverageAsync(ids[index]));
        Assert.Equal(0, Interlocked.Read(ref backfills));
        Assert.Equal(0, slots.Held);
    }

    [Fact]
    public async Task IdleReader_GivesItsBufferToAWaitingReader_AndResumesCorrectly()
    {
        await using var store = CreateStore();
        var slots = new NativeBufferSlots(1) { ReclaimIdleAfter = TimeSpan.FromMilliseconds(20) };
        var paused = Pattern(2 * Block, 3);
        var waiting = Pattern(2 * Block, 4);
        var pausedId = new NativeCacheIdentity("paused", "v1", paused.Length);
        var waitingId = new NativeCacheIdentity("waiting", "v1", waiting.Length);
        await SeedLastBlockAsync(store, waitingId, waiting);
        await using (var queue = new NativeCacheCommitQueue())
        {
            await using var idle = Stream(store, pausedId, paused, slots, queue);
            var actual = new byte[paused.Length];
            var prefix = await idle.ReadAsync(actual.AsMemory(0, 64 * 1024));
            Assert.True(prefix > 0);
            Assert.Equal(1, slots.Held); // A paused player mid-block still holds its buffer.

            var watch = Stopwatch.StartNew();
            await using (var other = Stream(store, waitingId, waiting, slots, queue, admissionWait: TimeSpan.FromSeconds(10)))
            {
                var otherBytes = new byte[waiting.Length];
                await other.ReadExactlyAsync(otherBytes);
                Assert.Equal(waiting, otherBytes);
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waiting reader took {watch.Elapsed}");
            await WaitUntilAsync(() => queue.QueuedBytes == 0); // Keep the new entry's first commit uncontended.

            await idle.ReadExactlyAsync(actual.AsMemory(prefix));
            Assert.Equal(paused, actual);
        }
        Assert.Equal(waiting.Length, await store.GetCoverageAsync(waitingId));
        // The resumed reader lost only its partly served block; the next one was cached.
        Assert.Equal(0, await store.GetMissingRangeBytesAsync(pausedId, Block, 2L * Block));
        Assert.Equal(0, slots.Held);
    }

    [Fact]
    public async Task ProbeTimeout_CostsOneBlock_SchedulesItsBackfill_AndKeepsTheDetachedBuffer()
    {
        await using var store = CreateStore();
        var slots = new NativeBufferSlots(2);
        var data = Pattern(5 * Block, 5);
        var id = new NativeCacheIdentity("slow-probe", "v1", data.Length);
        // Catalogued blocks are probed; blocks the catalogue lacks go straight to the source.
        Assert.True(await store.WriteBlockAsync(id, 0, data.AsMemory(0, Block)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var backfills = new List<(long Start, long Length)>();
        try
        {
            await using (var queue = new NativeCacheCommitQueue())
            {
                await using (var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(new CacheableSource(data)),
                    () => true, slots, writeBehind: true, commitQueue: queue, backfill: (start, length) => backfills.Add((start, length)))
                {
                    CacheIoTimeout = TimeSpan.FromMilliseconds(50),
                    BeforeCacheIo = async (write, _) =>
                    {
                        if (!write && Interlocked.Increment(ref probes) == 1) await release.Task;
                    },
                })
                {
                    var actual = new byte[data.Length];
                    await stream.ReadExactlyAsync(actual);
                    Assert.Equal(data, actual);
                    Assert.Equal(1, slots.Held); // Only the stalled probe still owns a buffer.
                }
            }
            Assert.Equal(new[] { (0L, (long)Block) }, backfills);
            Assert.Equal(0, await store.GetMissingRangeBytesAsync(id, Block, data.Length));
            Assert.Equal(1, slots.Held);
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => slots.Held == 0);
    }

    [Fact]
    public async Task FillAdmissionTimeout_CostsOneBlock_AndSchedulesItsBackfill()
    {
        await using var store = CreateStore();
        var data = Pattern(5 * Block, 6);
        var id = new NativeCacheIdentity("busy-fill", "v1", data.Length);
        await SeedLastBlockAsync(store, id, data);
        var backfills = new List<(long Start, long Length)>();
        using (await store.AcquireFillAsync(id, 0, CancellationToken.None))
        {
            await using var queue = new NativeCacheCommitQueue();
            await using var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(new CacheableSource(data)),
                () => true, new NativeBufferSlots(2), writeBehind: true, commitQueue: queue,
                backfill: (start, length) => backfills.Add((start, length)))
            { CacheIoTimeout = TimeSpan.FromMilliseconds(50) };
            var actual = new byte[data.Length];
            await stream.ReadExactlyAsync(actual);
            Assert.Equal(data, actual);
        }
        Assert.Equal(new[] { (0L, (long)Block) }, backfills);
        Assert.Equal(0, await store.GetMissingRangeBytesAsync(id, Block, data.Length));
    }

    [Fact]
    public async Task Warming_NeverTakesTheLastSlot_AndDefersWhenBusy()
    {
        await using var store = CreateStore();
        var slots = new NativeBufferSlots(2);
        using var otherWarming = await slots.AcquireAsync(background: true, TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(otherWarming);
        Assert.False(slots.CanAdmitBackground());
        Assert.Null(await slots.AcquireAsync(background: true, TimeSpan.Zero, CancellationToken.None));

        var data = Pattern(Block, 7);
        await using var warming = new NativeCachedStream(store, new NativeCacheIdentity("warm", "v1", data.Length),
            _ => Task.FromResult<Stream>(new CacheableSource(data)), () => true, slots, background: true)
        { BackgroundAdmissionWait = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAsync<NativeCacheBusyException>(() => warming.ReadWarmProbeAsync(new byte[1], CancellationToken.None).AsTask());

        // Playback still gets the reserved slot.
        using var playback = await slots.AcquireAsync(background: false, TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(playback);
        Assert.Equal(2, slots.Held);
    }

    private NativeCachedStream Stream(NativeCacheStore store, NativeCacheIdentity id, byte[] data, NativeBufferSlots slots,
        NativeCacheCommitQueue queue, TimeSpan? admissionWait = null, Action<long, long>? backfill = null) =>
        new(store, id, _ => Task.FromResult<Stream>(new CacheableSource(data)), () => true, slots,
            writeBehind: true, commitQueue: queue, backfill: backfill)
        { AdmissionWait = admissionWait ?? TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Creates each file's catalogue entry up front. The store does not queue a brand-new entry's
    /// first commit behind another entry's write (it reports writer_busy), which is separate from
    /// buffer admission; these tests isolate admission.
    /// </summary>
    private static async Task SeedLastBlockAsync(NativeCacheStore store, NativeCacheIdentity id, byte[] data)
    {
        var last = (data.Length - 1) / Block * Block;
        Assert.True(await store.WriteBlockAsync(id, last, data.AsMemory(last)));
    }

    private NativeCacheStore CreateStore() =>
        new(Path.Combine(_root, "index.db"), [new NativeCacheFolder { Path = _root, MinFreeBytes = 0 }]);

    private static byte[] Pattern(int length, int seed) =>
        Enumerable.Range(0, length).Select(index => (byte)(index * 13 + seed * 7)).ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }

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
