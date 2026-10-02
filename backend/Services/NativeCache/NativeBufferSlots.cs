using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>
/// Implemented by a stream that can give up an idle block buffer when another reader needs it.
/// </summary>
internal interface INativeBufferHolder
{
    /// <summary>
    /// Drops the holder's buffer and disposes its lease when it has been idle since
    /// <paramref name="idleCutoff"/> or earlier. False when it is reading or holds detached IO.
    /// </summary>
    bool TryReclaim(long idleCutoff);
}

/// <summary>
/// The Native Cache buffer budget as 4 MiB block slots. A stream holds a slot only while it fills
/// or verifies one block (and while NAS IO it started still uses that buffer), not for its
/// lifetime: once the block is ready the slot returns and the stream keeps just that block to
/// serve, like any streaming buffer. The budget therefore bounds concurrent block work and cache
/// IO, not concurrent responses. A slot handed to uncancellable NAS IO stays held until that IO
/// really finishes. Warming can never take the last slot, so playback always has one.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Managed-only semaphore: detached filesystem operations keep leases after shutdown and must still release them. AvailableWaitHandle is never used.")]
public sealed class NativeBufferSlots
{
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Lease, byte> _live = new();
    private readonly int _backgroundLimit;
    private int _backgroundHeld;
    private int _serving;

    public NativeBufferSlots(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new SemaphoreSlim(capacity, capacity);
        _backgroundLimit = Math.Max(1, capacity - 1);
    }

    /// <summary>Slots in the configured budget: <c>cache.native.writer-mb</c> ÷ 4.</summary>
    public static int CapacityFor(int bufferMb) => Math.Max(1, bufferMb / 4);

    public int Capacity { get; }
    public int Free => _slots.CurrentCount;
    public int Held => Capacity - Free;
    public long HeldBytes => Held * (long)NativeCacheStore.BlockSize;

    /// <summary>Ready blocks streams keep to serve after returning their slot.</summary>
    public int Serving => Volatile.Read(ref _serving);

    internal void AddServing(int delta) => Interlocked.Add(ref _serving, delta);

    /// <summary>How long a reader must have been idle before a waiting reader may take its buffer.</summary>
    public TimeSpan ReclaimIdleAfter { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// True when a warming stream could take a slot now (it never takes the last one), so a
    /// warming job is deferred before it opens any source.
    /// </summary>
    public bool CanAdmitBackground() =>
        Volatile.Read(ref _backgroundHeld) < _backgroundLimit && (Free > 0 || HasIdleHolder());

    /// <summary>
    /// Takes a slot: a free one, else one reclaimed from a reader idle for at least
    /// <see cref="ReclaimIdleAfter"/>, else the first freed within <paramref name="wait"/>.
    /// Null when none became available in time.
    /// </summary>
    public async ValueTask<Lease?> AcquireAsync(bool background, TimeSpan wait, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (background)
        {
            while (!TryReserveBackground())
            {
                var remaining = wait - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) return null;
                await Task.Delay(Min(remaining, ReclaimPoll), cancellationToken).ConfigureAwait(false);
            }
        }
        var acquired = false;
        try
        {
            acquired = _slots.Wait(0, CancellationToken.None) || (TryReclaimIdle() && _slots.Wait(0, CancellationToken.None));
            if (!acquired)
            {
                var remaining = wait - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero) acquired = await WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            if (!acquired) return null;
            var lease = new Lease(this, background);
            _live.TryAdd(lease, 0);
            return lease;
        }
        finally
        {
            if (!acquired && background) Interlocked.Decrement(ref _backgroundHeld);
        }
    }

    private TimeSpan ReclaimPoll => Min(ReclaimIdleAfter, TimeSpan.FromMilliseconds(100));
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private async Task<bool> WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        // One queued wait keeps the semaphore's first-come order; idle readers that appear while
        // waiting are reclaimed on a short poll and their slot goes to the head of that queue.
        var acquire = _slots.WaitAsync(wait, cancellationToken);
        while (!acquire.IsCompleted)
        {
            await Task.WhenAny(acquire, Task.Delay(ReclaimPoll, CancellationToken.None)).ConfigureAwait(false);
            if (!acquire.IsCompleted) TryReclaimIdle();
        }
        return await acquire.ConfigureAwait(false);
    }

    private bool TryReserveBackground()
    {
        while (true)
        {
            var held = Volatile.Read(ref _backgroundHeld);
            if (held >= _backgroundLimit) return false;
            if (Interlocked.CompareExchange(ref _backgroundHeld, held + 1, held) == held) return true;
        }
    }

    private bool HasIdleHolder()
    {
        foreach (var pair in _live)
            if (Volatile.Read(ref pair.Key.IdleSince) != 0) return true;
        return false;
    }

    /// <summary>Takes the buffer of the reader idle the longest, if one has been idle long enough.</summary>
    private bool TryReclaimIdle()
    {
        var cutoff = Stopwatch.GetTimestamp() - (long)(ReclaimIdleAfter.TotalSeconds * Stopwatch.Frequency);
        Lease? oldest = null;
        var oldestIdle = long.MaxValue;
        foreach (var pair in _live)
        {
            var idle = Volatile.Read(ref pair.Key.IdleSince);
            if (idle != 0 && idle <= cutoff && idle < oldestIdle)
            {
                oldest = pair.Key;
                oldestIdle = idle;
            }
        }
        return oldest?.Holder?.TryReclaim(cutoff) == true;
    }

    /// <summary>One held slot. Disposing it returns the slot; a holder marks it idle between reads.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly NativeBufferSlots _owner;
        private readonly bool _background;
        private int _disposed;
        internal long IdleSince;
        internal INativeBufferHolder? Holder;

        internal Lease(NativeBufferSlots owner, bool background)
        {
            _owner = owner;
            _background = background;
        }

        /// <summary>Lets a waiting reader reclaim this slot once it has stayed idle long enough.</summary>
        internal void MarkIdle(INativeBufferHolder holder)
        {
            Holder = holder;
            Volatile.Write(ref IdleSince, Math.Max(1, Stopwatch.GetTimestamp()));
        }

        internal void MarkBusy() => Volatile.Write(ref IdleSince, 0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref IdleSince, 0);
            Holder = null;
            _owner._live.TryRemove(this, out _);
            if (_background) Interlocked.Decrement(ref _owner._backgroundHeld);
            _owner._slots.Release();
        }
    }
}
