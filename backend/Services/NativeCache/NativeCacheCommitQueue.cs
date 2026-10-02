using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.Observability;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>
/// Durable cache commits off the playback path. A foreground stream hands over a private copy of
/// each verified block and continues immediately; workers publish blocks without a deadline, so a
/// slow NAS delays the cache, never the player. A bounded byte budget caps memory: when it is full,
/// or a commit fails, the caller is told so it can schedule a backfill instead of skipping silently.
/// </summary>
public sealed class NativeCacheCommitQueue : IAsyncDisposable
{
    public const long DefaultCapacityBytes = 256L * 1024 * 1024;
    private readonly Channel<Item> _items = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task[] _workers;
    private readonly long _capacityBytes;
    private long _queuedBytes;

    public NativeCacheCommitQueue(long capacityBytes = DefaultCapacityBytes, int workers = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, NativeCacheStore.BlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _capacityBytes = capacityBytes;
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(RunAsync)).ToArray();
    }

    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>
    /// Copies <paramref name="block"/> and queues its publication. False when the byte budget is
    /// full or the queue is stopping; the caller still owns <paramref name="block"/> either way.
    /// <paramref name="completed"/> runs on a worker with whether the block was published.
    /// </summary>
    public bool TryEnqueue(NativeCacheStore store, NativeCacheIdentity identity, long offset,
        ReadOnlySpan<byte> block, Action<bool> completed)
    {
        if (_stopping.IsCancellationRequested) return false;
        if (Interlocked.Add(ref _queuedBytes, block.Length) > _capacityBytes)
        {
            Interlocked.Add(ref _queuedBytes, -block.Length);
            return false;
        }
        var copy = ArrayPool<byte>.Shared.Rent(block.Length);
        block.CopyTo(copy);
        // The lease keeps the entry from eviction or scans while its block waits to publish.
#pragma warning disable CA2000 // Ownership moves to the queued item; Release disposes it.
        var item = new Item(store, identity, offset, copy, block.Length, store.AcquireLease(identity), completed,
            Stopwatch.GetTimestamp());
#pragma warning restore CA2000
        if (_items.Writer.TryWrite(item)) return true;
        Release(item);
        return false;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _items.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
                await CommitAsync(item).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task CommitAsync(Item item)
    {
        var started = Stopwatch.GetTimestamp();
        PrometheusMetrics.Current?.RecordNativeCachePhase("queue_wait", Stopwatch.GetElapsedTime(item.Queued, started));
        var committed = false;
        try
        {
            committed = await item.Store.WriteBlockAsync(item.Identity, item.Offset, item.Buffer.AsMemory(0, item.Count),
                waitForWriter: true, cancellationToken: _stopping.Token).ConfigureAwait(false);
            PrometheusMetrics.Current?.RecordNativeCachePhase("commit", Stopwatch.GetElapsedTime(started));
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException
            or OperationCanceledException or ObjectDisposedException) { }
        finally { Release(item); }
        try { item.Completed(committed); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* Never fault a worker. */ }
    }

    private void Release(Item item)
    {
        item.Lease.Dispose();
        ArrayPool<byte>.Shared.Return(item.Buffer);
        Interlocked.Add(ref _queuedBytes, -item.Count);
    }

    /// <summary>
    /// Stops accepting work and gives in-flight commits a short grace period. Blocks still queued
    /// are reported as not published so their streams' backfill requests cover them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _items.Writer.TryComplete();
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        await _stopping.CancelAsync().ConfigureAwait(false);
        // Let cancelled commits report before draining, so each block is reported exactly once
        // even when shutdown interrupts it. A NAS call that ignores cancellation is not awaited forever.
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        while (_items.Reader.TryRead(out var item))
        {
            Release(item);
            try { item.Completed(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        _stopping.Dispose();
    }

    private sealed record Item(NativeCacheStore Store, NativeCacheIdentity Identity, long Offset, byte[] Buffer, int Count,
        IDisposable Lease, Action<bool> Completed, long Queued);
}
