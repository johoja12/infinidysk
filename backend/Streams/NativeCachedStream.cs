using System.Buffers;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.WebDav.Requests;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Final-byte, lazy-source read-through stream. Never interprets sparse holes as data.
/// A buffer slot is held only while one block is filled or verified: it returns as soon as the
/// block is ready (the stream keeps just that block to serve) and is re-acquired for the next one,
/// and a reader idle mid-fill gives it up when another reader needs it. A response starting on a
/// cold block reads its first source bytes before taking a slot, so start-up never holds one. A
/// block that cannot get a slot, a fill lease or a timely cache probe is served from the source
/// and noted for backfill; only that block is affected, never the rest of the response.
/// </summary>
public sealed class NativeCachedStream : FastReadOnlyStream, ICacheReadEvidence, IStreamGenerationEvidence, INativeBufferHolder
{
    private const int SourceWindowBytes = 4 * NativeCacheStore.BlockSize;
    private readonly NativeCacheStore _store;
    private readonly NativeCacheIdentity _identity;
    private readonly Func<CancellationToken, Task<Stream>> _openSource;
    private readonly Func<bool> _generationIsCurrent;
    private readonly IDisposable _lease;
    private readonly NativeBufferSlots? _slots;
    private readonly IDisposable? _owned;
    private NativeBufferSlots.Lease? _bufferAdmission;
    private readonly object _holdGate = new();
    private bool _idle;
    private long _idleSince;
    private bool _serving;
    private readonly bool _background;
    private readonly bool _writeBehind;
    private Task<bool>? _pendingWrite;
    private IDisposable? _activeFill;
    private readonly NativeCacheStatistics? _statistics;
    private NativeCacheStatistics.NativeCacheTransfer? _transfer;
    private Stream? _source;
    private long _sourceWindowEnd = -1;
    private long _foregroundWindowBytes = SourceWindowBytes;
    private long _reopenedFillBlock = -1;
    private bool _abandonLogged;
    private readonly NativeCacheCommitQueue? _commitQueue;
    private readonly Action<long, long, string>? _backfill;
    private readonly UncachedRangeTracker _uncached;
    private readonly List<Task> _queuedCommits = [];
    private long? _responseEnd;
    private byte[]? _buffer;
    private long _bufferStart = -1;
    private int _bufferCount;
    private bool _bufferVerified;
    private bool _bufferFromCache;
    private long _position;
    private bool _disposed;
    private bool _bypassFill;
    private bool _servedBytes;
    private bool _untrackedSource;
    private long _lastDirectMissBlock = -1;
    private long _directBlockStart = -1;
    private long _fillStarted;
    internal TimeSpan CacheIoTimeout { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// How long a foreground block waits, first come first served, for a buffer slot before it is
    /// served from the source and noted for backfill.
    /// </summary>
    internal TimeSpan AdmissionWait { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan BackgroundAdmissionWait { get; init; } = TimeSpan.FromSeconds(30);
    internal Func<bool, CancellationToken, Task>? BeforeCacheIo { get; init; }

    public NativeCachedStream(NativeCacheStore store, NativeCacheIdentity identity,
        Func<CancellationToken, Task<Stream>> openSource, Func<bool> generationIsCurrent,
        NativeBufferSlots? bufferSlots = null, bool background = false, NativeCacheStatistics? statistics = null, bool writeBehind = false,
        NativeCacheCommitQueue? commitQueue = null, Action<long, long, string>? backfill = null, IDisposable? ownedResource = null)
    {
        _commitQueue = background ? null : commitQueue;
        _backfill = background ? null : backfill;
        _uncached = new UncachedRangeTracker(identity.Length, _backfill, identity.DisplayName ?? identity.ItemId);
        _store = store;
        _identity = identity;
        _openSource = openSource;
        _generationIsCurrent = generationIsCurrent;
        _lease = store.AcquireLease(identity);
        _slots = bufferSlots;
        _owned = ownedResource;
        _background = background;
        _writeBehind = writeBehind && !background;
        _statistics = statistics;
    }

    public bool LastReadCacheable { get; private set; }
    /// <summary>
    /// The source failure that last made this stream stop filling the cache, for warming to classify
    /// (missing or foreign articles versus a transient transport error). Null when none occurred.
    /// </summary>
    internal Exception? LastFillFailure { get; private set; }
    /// <summary>
    /// Start of the last block whose source bytes arrived without integrity proof (gap-filled, CRC
    /// mismatch, or a different post), or -1. Warming treats repeated failures here as source damage.
    /// </summary>
    internal long LastUnverifiedSourceBlock { get; private set; } = -1;
    public NativeCacheIdentity Identity => _identity;
    public string GenerationIdentity => _identity.Key;
    public bool IsSourceCurrent => _generationIsCurrent();
    public override long Length => _identity.Length;
    public override bool CanSeek => true;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }

    /// <summary>Checks existing bytes without opening the source or spending a warming budget.</summary>
    internal async Task<bool> VerifyCachedBlockAsync(long blockStart, CancellationToken cancellationToken)
    {
        var valid = await _store.VerifyOnceAsync(_identity, blockStart,
            () => VerifyOwnedBlockAsync(blockStart, cancellationToken), cancellationToken).ConfigureAwait(false);
        return valid && _generationIsCurrent();
    }

    private async Task<bool> VerifyOwnedBlockAsync(long blockStart, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_bypassFill || !_generationIsCurrent()) return false;
        EnterRead();
        try
        {
            // Use this stream's admitted buffer and IO lifetime handling. Cancellation
            // must not return memory still owned by an uncancellable NAS read.
            if (!await EnsureBufferAsync(cancellationToken).ConfigureAwait(false)) return false;
            _bufferStart = -1;
            _bufferCount = 0;
            _bufferVerified = false;
            _bufferFromCache = false;
            LastReadCacheable = false;
            var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
            var buffer = _buffer!;
            var count = await CacheIoAsync(false, "probe",
                token => _store.ReadBlockAsync(_identity, blockStart, buffer.AsMemory(0, expected), token),
                cancellationToken).ConfigureAwait(false);
            return count == expected && _generationIsCurrent();
        }
        finally { ParkBuffer(); }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) =>
        ReadHoldingBufferAsync(destination, completeBlock: false, cancellationToken);

    internal ValueTask<int> ReadWarmProbeAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        ReadHoldingBufferAsync(destination, completeBlock: true, cancellationToken);

    private async ValueTask<int> ReadHoldingBufferAsync(Memory<byte> destination, bool completeBlock, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnterRead();
        try { return await ReadCoreAsync(destination, completeBlock, cancellationToken).ConfigureAwait(false); }
        finally { ParkBuffer(); }
    }

    /// <summary>Stops other readers reclaiming this stream's buffer while it reads.</summary>
    private void EnterRead()
    {
        lock (_holdGate) _idle = false;
        _bufferAdmission?.MarkBusy();
    }

    /// <summary>
    /// After a read: keeps the buffer only while it still holds unserved bytes of the current block
    /// (or a fill in progress) for this response, otherwise returns it. A ready block keeps no slot;
    /// a fill in progress keeps its slot as a reclaimable idle holder until the next read.
    /// </summary>
    private void ParkBuffer()
    {
        if (_buffer is null || _disposed) return;
        // The legacy write-behind commit still reads the buffer; the next block or disposal drains it.
        if (_pendingWrite is { IsCompleted: false }) return;
        var blockEnd = _bufferStart < 0 ? -1 : Math.Min(Length, _bufferStart + NativeCacheStore.BlockSize);
        var keep = !_bypassFill && _bufferStart >= 0
            && _position >= _bufferStart && _position < blockEnd && _position <= _bufferStart + _bufferCount
            && (_responseEnd is not { } end || _position < end);
        if (!keep)
        {
            DropBuffer();
            return;
        }
        if (_bufferFromCache || _bufferCount == blockEnd - _bufferStart) ReleaseSlotKeepBuffer();
        if (_bufferAdmission is null) return;
        lock (_holdGate)
        {
            _idle = true;
            _idleSince = Stopwatch.GetTimestamp();
        }
        _bufferAdmission.MarkIdle(this);
    }

    /// <summary>
    /// The block is ready (verified from cache, or filled and handed to its commit): return the slot
    /// and keep only this block to serve. A legacy write-behind still reading it keeps the slot.
    /// </summary>
    private void ReleaseSlotKeepBuffer()
    {
        if (_bufferAdmission is null || _pendingWrite is { IsCompleted: false }) return;
        var admission = _bufferAdmission;
        _bufferAdmission = null;
        if (_buffer is not null && !_serving)
        {
            _serving = true;
            _slots?.AddServing(1);
        }
        admission.Dispose();
    }

    private void StopServing()
    {
        if (!_serving) return;
        _serving = false;
        _slots?.AddServing(-1);
    }

    bool INativeBufferHolder.TryReclaim(long idleCutoff)
    {
        lock (_holdGate)
        {
            // Only between reads, so no read or detached IO uses the buffer; EnterRead and
            // disposal take this gate before touching it again.
            if (!_idle || _idleSince > idleCutoff) return false;
            _idle = false;
            DropBuffer();
        }
        return true;
    }

    /// <summary>Returns the block buffer and its slot. Never call while detached IO still uses the buffer.</summary>
    private void DropBuffer()
    {
        _activeFill?.Dispose();
        _activeFill = null;
        _bufferStart = -1;
        _bufferCount = 0;
        _bufferVerified = false;
        _bufferFromCache = false;
        var buffer = _buffer;
        var admission = _bufferAdmission;
        _buffer = null;
        _bufferAdmission = null;
        StopServing();
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        admission?.Dispose();
    }

    /// <summary>
    /// Rents this block's buffer under a buffer slot. False when a foreground block found no slot in
    /// time; it is then served from the source. Warming throws so its job defers instead.
    /// </summary>
    private async ValueTask<bool> EnsureBufferAsync(CancellationToken cancellationToken)
    {
        if (_slots is not null && _bufferAdmission is null)
        {
            var wait = _background ? BackgroundAdmissionWait : AdmissionWait;
            var waitStarted = Stopwatch.GetTimestamp();
            var admission = await _slots.AcquireAsync(_background, wait, cancellationToken).ConfigureAwait(false);
            RecordPhase("buffer_admission", waitStarted);
            if (admission is null)
            {
                if (_background) throw new NativeCacheBusyException("Native cache buffers are busy; warming resumes later.");
                return false;
            }
            _bufferAdmission = admission;
            StopServing();
        }
        _buffer ??= ArrayPool<byte>.Shared.Rent(NativeCacheStore.BlockSize);
        return true;
    }

    /// <summary>
    /// True when the catalogue has no part of this block, so it must come from the source and needs
    /// no cache probe. Unknown (slow or failing catalogue) is false: the ordinary probe decides.
    /// </summary>
    private async Task<bool> IsBlockMissingAsync(long blockStart, int expected, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.FindNextMissingOffsetAsync(_identity, blockStart, blockStart + expected, cancellationToken)
                .WaitAsync(CacheIoTimeout, cancellationToken).ConfigureAwait(false) == blockStart;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or SqliteException
            or UnauthorizedAccessException or ObjectDisposedException)
        { return false; }
    }

    /// <summary>Serves the rest of the current block from the source; the tracker notes it for backfill.</summary>
    private ValueTask<int> ReadDirectBlockAsync(Memory<byte> destination, long blockStart, CancellationToken cancellationToken)
    {
        _directBlockStart = blockStart;
        return ReadSourceRangeAsync(destination[..(int)Math.Min(destination.Length,
            blockStart + NativeCacheStore.BlockSize - _position)], cancellationToken);
    }

    private const int ColdFillBuffered = -1;
    private const int ColdFillReopened = -2;

    /// <summary>
    /// Starts a response on a block the catalogue lacks: opens the source and reads the first chunk
    /// before taking a buffer slot, so source start-up (connections, first round trip) never
    /// holds a slot.
    /// Returns <see cref="ColdFillBuffered"/> when the chunk now begins this block's buffer under a
    /// slot, <see cref="ColdFillReopened"/> to retry on a reopened source, or the bytes it served
    /// straight from the source because no slot became free (the block is noted for backfill).
    /// </summary>
    private async ValueTask<int> StartColdFillAsync(Memory<byte> destination, long blockStart, int expected,
        CancellationToken cancellationToken)
    {
        _statistics?.Miss();
        _lastDirectMissBlock = blockStart;
        var prefix = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            bool verified;
            try
            {
                _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
                PositionSourceForFill(_source, blockStart, completeBlock: false);
                _fillStarted = Stopwatch.GetTimestamp();
                read = await ReadSourceForFillAsync(_source, prefix.AsMemory(0, Math.Min(expected, Math.Min(destination.Length, 64 * 1024))),
                    cancellationToken).ConfigureAwait(false);
                StreamStartupTrace.TryRecord(StreamStartupPhase.NativeSourceFirstRead,
                    bytes: read, elapsed: Stopwatch.GetElapsedTime(_fillStarted));
                if (read == 0) throw new EndOfStreamException("Source ended before the declared media length.");
                _statistics?.SourceBytes(read);
                verified = _source is ICacheReadEvidence { LastReadCacheable: true };
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                if (await TryReopenFillAsync(blockStart, exception).ConfigureAwait(false)) return ColdFillReopened;
                if (_source is not null)
                {
                    try { await _source.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception teardown) when (teardown is not OutOfMemoryException) { }
                }
                _source = null;
                _bypassFill = true;
                _activeFill?.Dispose();
                _activeFill = null;
                if (exception is not TimeoutException) _statistics?.Fallback();
                LastFillFailure = exception;
                RecordSkip("fill_source_failure", abandonsResponse: true);
                return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            bool admitted;
            try { admitted = await EnsureBufferAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                _activeFill?.Dispose();
                _activeFill = null;
                throw;
            }
            if (!admitted)
            {
                RecordSkip("admission_overflow", abandonsResponse: false);
                return ServeColdPrefix(prefix, read, destination, blockStart);
            }
            prefix.AsSpan(0, read).CopyTo(_buffer);
            _bufferStart = blockStart;
            _bufferCount = read;
            _bufferVerified = verified;
            _bufferFromCache = false;
            if (read == expected)
            {
                // A short final block is complete already.
                try { await CompleteFillAsync(blockStart, expected, cancellationToken).ConfigureAwait(false); }
                catch (CacheIoTimeoutException) { return ServeColdPrefix(prefix, read, destination, blockStart); }
            }
            return ColdFillBuffered;
        }
        finally { ArrayPool<byte>.Shared.Return(prefix); }
    }

    /// <summary>Serves a cold block's first chunk without caching; the rest of the block follows from the source.</summary>
    private int ServeColdPrefix(byte[] prefix, int read, Memory<byte> destination, long blockStart)
    {
        _activeFill?.Dispose();
        _activeFill = null;
        _directBlockStart = blockStart;
        _sourceWindowEnd = -1;
        prefix.AsSpan(0, read).CopyTo(destination.Span);
        _uncached.Note(_position, _position + read);
        KeepResponseGeneration(); // These bytes came from the source, so they stay valid.
        _position += read;
        _servedBytes = true;
        _uncached.Served(read);
        LastReadCacheable = false;
        return read;
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, bool completeBlock, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastReadCacheable = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty || _position == Length) return 0;
        var responseBudget = RangeContext.GetReadBudget();
        _responseEnd ??= _position + Math.Min(Length - _position,
            responseBudget is > 0 ? responseBudget.Value : Length - _position);
        if (!_untrackedSource && !_generationIsCurrent())
        {
            if (_servedBytes) throw new MediaSourceChangedException("Media source changed during this response. Retry the range against the current source.");
            _untrackedSource = true;
            _uncached.Cause = BackfillMissReasons.SourceChanged;
            _bypassFill = true;
        }
        if (_bypassFill) return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
        var blockStart = _position / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        if (_directBlockStart == blockStart)
            return await ReadSourceRangeAsync(destination[..(int)Math.Min(destination.Length,
                blockStart + NativeCacheStore.BlockSize - _position)], cancellationToken).ConfigureAwait(false);
        if (_bufferFromCache && !_generationIsCurrent()) _bufferStart = -1;
        if (_bufferStart != blockStart)
        {
            _activeFill?.Dispose();
            _activeFill = null;
            if (_pendingWrite is { } pending)
            {
                try { await CacheIoAsync(true, "pending_write", _ => pending, cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false); }
                finally { _pendingWrite = null; }
            }
            _activeFill = await AcquireFillAsync(blockStart, cancellationToken).ConfigureAwait(false);
            if (_activeFill is null) return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false);
            var blockLength = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
            // Only a response's first block: later blocks read whole blocks from an already running
            // pipeline, where an extra small read would only fragment its batches.
            if (!_background && !completeBlock && !_servedBytes && _position == blockStart && _generationIsCurrent()
                && await IsBlockMissingAsync(blockStart, blockLength, cancellationToken).ConfigureAwait(false))
            {
                var direct = await StartColdFillAsync(destination, blockStart, blockLength, cancellationToken).ConfigureAwait(false);
                if (direct == ColdFillReopened) return await ReadCoreAsync(destination, completeBlock, cancellationToken).ConfigureAwait(false);
                if (direct >= 0) return direct;
            }
            else
            {
                bool admitted;
                try { admitted = await EnsureBufferAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    _activeFill.Dispose();
                    _activeFill = null;
                    throw;
                }
                if (!admitted)
                {
                    _activeFill.Dispose();
                    _activeFill = null;
                    RecordSkip("admission_overflow", abandonsResponse: false);
                    return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false);
                }
                _bufferStart = -1;
                _bufferCount = 0;
                _bufferVerified = false;
                _bufferFromCache = false;
                var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
                if (_generationIsCurrent())
                {
                    var probeStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        var buffer = _buffer!;
                        _bufferCount = await CacheIoAsync(false, "probe", token => _store.ReadBlockAsync(_identity, blockStart,
                            buffer.AsMemory(0, expected), token), cancellationToken).ConfigureAwait(false);
                        _bufferVerified = _bufferCount == expected;
                        _bufferFromCache = _bufferVerified;
                        if (!_generationIsCurrent()) _bufferCount = 0;
                        if (_bufferCount == expected)
                        {
                            _statistics?.Hit(expected);
                            _activeFill?.Dispose();
                            _activeFill = null;
                            ReleaseSlotKeepBuffer();
                        }
                    }
                    catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
                    { /* Cache storage failures never prevent source playback. */ }
                    catch (TimeoutException)
                    {
                        // A slow probe costs this block only: its buffer stays with the detached read.
                        _activeFill?.Dispose();
                        _activeFill = null;
                        return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        StreamStartupTrace.TryRecord(StreamStartupPhase.NativeCacheProbe,
                            elapsed: Stopwatch.GetElapsedTime(probeStarted));
                    }
                }
                if (_bufferCount != expected)
                {
                    _statistics?.Miss();
                    _lastDirectMissBlock = blockStart;
                    // A seek into a cold block should not fetch its unrequested prefix
                    // before the first response byte. The next aligned block can fill.
                    if (_position != blockStart)
                    {
                        StreamStartupTrace.TryRecord(StreamStartupPhase.NativeSeekBypass);
                        _activeFill?.Dispose();
                        _activeFill = null;
                        return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false);
                    }
                    try
                    {
                        _bufferFromCache = false;
                        _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
                        PositionSourceForFill(_source, blockStart, completeBlock);
                        _bufferCount = 0;
                        _bufferVerified = true;
                        _fillStarted = Stopwatch.GetTimestamp();
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        if (await TryReopenFillAsync(blockStart, exception).ConfigureAwait(false))
                            return await ReadCoreAsync(destination, completeBlock, cancellationToken).ConfigureAwait(false);
                        // Read-ahead is speculative. A later missing article must not
                        // break a readable prefix/range requested by the player. Reopen
                        // without native proof/read-ahead context and use ordinary reads.
                        if (_source is not null)
                        {
                            try { await _source.DisposeAsync().ConfigureAwait(false); }
                            catch (Exception teardown) when (teardown is not OutOfMemoryException) { }
                        }
                        _source = null;
                        _bufferCount = 0;
                        _bufferVerified = false;
                        _bypassFill = true;
                        _activeFill?.Dispose();
                        _activeFill = null;
                        if (exception is not TimeoutException) _statistics?.Fallback();
                        LastFillFailure = exception;
                        RecordSkip("fill_source_failure", abandonsResponse: true);
                        return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
                    }
                }
                _bufferStart = blockStart;
            }
        }
        var bufferOffset = checked((int)(_position - _bufferStart));
        if (!_bufferFromCache && bufferOffset >= _bufferCount)
        {
            if (bufferOffset > _bufferCount)
            {
                _activeFill?.Dispose();
                _activeFill = null;
                return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
                // Return the first response chunk as soon as its source bytes are
                // verified. Thereafter fill whole blocks: repeated tiny source
                // reads starve the NNTP batch pipeline on sustained transfers.
                var target = !completeBlock && !_servedBytes && _bufferCount == 0
                    ? Math.Min(expected, bufferOffset + Math.Min(destination.Length, 64 * 1024))
                    : expected;
                while (_bufferCount < target)
                {
                    var firstRead = _bufferCount == 0;
                    var readStarted = firstRead ? Stopwatch.GetTimestamp() : 0;
                    var read = await ReadSourceForFillAsync(_source!, _buffer!.AsMemory(_bufferCount, target - _bufferCount), cancellationToken)
                        .ConfigureAwait(false);
                    if (firstRead)
                        StreamStartupTrace.TryRecord(StreamStartupPhase.NativeSourceFirstRead,
                            bytes: read, elapsed: Stopwatch.GetElapsedTime(readStarted));
                    if (read == 0) throw new EndOfStreamException("Source ended before the declared media length.");
                    _statistics?.SourceBytes(read);
                    if (_source is not ICacheReadEvidence { LastReadCacheable: true })
                    {
                        _bufferVerified = false;
                        LastUnverifiedSourceBlock = blockStart;
                    }
                    _bufferCount += read;
                }
                if (_bufferCount == expected) await CompleteFillAsync(blockStart, expected, cancellationToken).ConfigureAwait(false);
            }
            catch (CacheIoTimeoutException)
            {
                // The commit outlived its deadline and kept the filled buffer: serve this block from
                // the source and continue caching the next one.
                _activeFill?.Dispose();
                _activeFill = null;
                return await ReadDirectBlockAsync(destination, blockStart, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                if (await TryReopenFillAsync(blockStart, exception).ConfigureAwait(false))
                    return await ReadCoreAsync(destination, completeBlock, cancellationToken).ConfigureAwait(false);
                if (_source is not null)
                {
                    try { await _source.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception teardown) when (teardown is not OutOfMemoryException) { }
                }
                _source = null;
                _bufferCount = 0;
                _bufferVerified = false;
                _bypassFill = true;
                _activeFill?.Dispose();
                _activeFill = null;
                if (exception is not TimeoutException) _statistics?.Fallback();
                LastFillFailure = exception;
                RecordSkip("fill_source_failure", abandonsResponse: true);
                return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        if (!KeepResponseGeneration())
            return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
        var count = Math.Min(destination.Length, _bufferCount - bufferOffset);
        _buffer!.AsMemory(bufferOffset, count).CopyTo(destination);
        if (!KeepResponseGeneration())
            return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
        _position += count;
        _servedBytes |= count > 0;
        _uncached.Served(count);
        LastReadCacheable = _bufferVerified && _generationIsCurrent();
        return count;
    }

    /// <summary>Commits a completely filled block, then returns its slot and keeps the block to serve.</summary>
    private async Task CompleteFillAsync(long blockStart, int expected, CancellationToken cancellationToken)
    {
        StreamStartupTrace.TryRecord(StreamStartupPhase.NativeFill,
            bytes: expected, elapsed: Stopwatch.GetElapsedTime(_fillStarted));
        await CommitFilledBlockAsync(blockStart, expected, cancellationToken).ConfigureAwait(false);
        if (!_writeBehind || _pendingWrite is null)
        {
            _activeFill?.Dispose();
            _activeFill = null;
        }
        ReleaseSlotKeepBuffer();
    }

    private async Task CommitFilledBlockAsync(long blockStart, int expected, CancellationToken cancellationToken)
    {
        if (!_bufferVerified || !_generationIsCurrent()) return;
        try
        {
            _transfer ??= _statistics?.BeginTransfer(_identity.ItemId, _identity.DisplayName, _identity.Length, _background);
            var buffer = _buffer!;
            if (_commitQueue is not null)
            {
                // Playback never waits for NAS durability: the queue publishes a private copy, and a
                // block it cannot take or publish is scheduled for backfill rather than skipped.
                var statistics = _statistics;
                var transfer = _transfer;
                var backfill = _backfill;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_commitQueue.TryEnqueue(_store, _identity, blockStart, buffer.AsSpan(0, expected), committed =>
                    {
                        try
                        {
                            if (committed) { statistics?.Committed(expected); transfer?.Committed(expected); return; }
                            PrometheusMetrics.Current?.RecordNativeCacheSkip("commit_failed");
                            backfill?.Invoke(blockStart, expected, BackfillMissReasons.WriteFailed);
                        }
                        finally { done.TrySetResult(); }
                    }))
                {
                    _queuedCommits.RemoveAll(task => task.IsCompleted);
                    _queuedCommits.Add(done.Task);
                }
                else
                {
                    RecordSkip("commit_queue_full", abandonsResponse: false);
                    _uncached.Note(blockStart, blockStart + expected);
                }
                return;
            }
            if (_writeBehind)
            {
                // The buffer stays immutable until this write finishes. The next
                // block and disposal drain it or transfer ownership on timeout.
                _pendingWrite = Task.Run(async () =>
                {
                    try
                    {
                        var started = Stopwatch.GetTimestamp();
                        if (BeforeCacheIo is not null) await BeforeCacheIo(true, cancellationToken).ConfigureAwait(false);
                        var committed = await _store.WriteBlockAsync(_identity, blockStart, buffer.AsMemory(0, expected),
                            waitForWriter: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                        RecordPhase("commit", started);
                        if (committed) { _statistics?.Committed(expected); _transfer?.Committed(expected); }
                        else RecordSkip("commit_rejected", abandonsResponse: false);
                        return committed;
                    }
                    catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException or OperationCanceledException)
                    { return false; }
                }, CancellationToken.None);
            }
            else if (await CacheIoAsync(true, "commit", token => _store.WriteBlockAsync(_identity, blockStart, buffer.AsMemory(0, expected),
                waitForWriter: _background, cancellationToken: token), cancellationToken)
                .ConfigureAwait(false)) { _statistics?.Committed(expected); _transfer?.Committed(expected); }
            else
            {
                RecordSkip("commit_rejected", abandonsResponse: false);
                _uncached.Note(blockStart, blockStart + expected);
            }
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A long-lived source pipeline can fail transiently (a dropped connection or a segment
    /// timeout far ahead of the reader). Retry the block once on a fresh, bounded pipeline
    /// before abandoning cache fills for the rest of the response; a block that fails again
    /// (for example a truly missing article) falls back to ordinary source reads as before.
    /// </summary>
    private async ValueTask<bool> TryReopenFillAsync(long blockStart, Exception exception)
    {
        if (_reopenedFillBlock == blockStart) return false;
        _reopenedFillBlock = blockStart;
        Log.Debug("Native cache fill at {Offset} reopening its source after: {Reason}", blockStart, exception.Message);
        if (_source is not null)
        {
            try { await _source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception teardown) when (teardown is not OutOfMemoryException) { }
        }
        _source = null;
        _sourceWindowEnd = -1;
        _bufferStart = -1;
        _bufferCount = 0;
        _bufferVerified = false;
        _activeFill?.Dispose();
        _activeFill = null;
        return true;
    }

    private void PositionSourceForFill(Stream source, long position, bool completeBlock)
    {
        // Keep the NNTP pipeline alive across integrity blocks. Seeking even to
        // the current offset in proof context tears down its finite segment plan.
        if (source.Position == position && position < _sourceWindowEnd) return;
        if (!_background && !completeBlock)
        {
            // Each window rebuilds the NNTP pipeline from cold with lookahead bounded by the
            // window, which caps distant providers at a handful of connections. Once a window is
            // exhausted by sequential reading (playback, not a probe) the source stays open to the
            // end of the response, so the pipeline is not rebuilt and its prefetch ceiling bounds
            // lookahead as with the cache off. Any seek starts with a bounded window again.
            _foregroundWindowBytes = source.Position == position && position == _sourceWindowEnd
                ? long.MaxValue
                : SourceWindowBytes;
        }
        var windowBytes = _background || completeBlock ? NativeCacheStore.BlockSize : _foregroundWindowBytes;
        var remaining = Math.Min(Length - position, windowBytes);
        if (_responseEnd is { } responseEnd && responseEnd > position)
        {
            // Integrity admission needs the whole final block, even for a short
            // HTTP range, but must not prefetch an additional window past it.
            var requested = Math.Min(remaining, responseEnd - position);
            var rounded = ((requested - 1) / NativeCacheStore.BlockSize + 1) * NativeCacheStore.BlockSize;
            remaining = Math.Min(remaining, rounded);
        }
        _sourceWindowEnd = position + remaining;
        using var context = new NativeCacheReadContext(remaining);
        source.Position = position;
    }

    private async ValueTask<int> ReadSourceForFillAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        using var context = new NativeCacheReadContext(_sourceWindowEnd - source.Position);
        return await source.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IDisposable?> AcquireFillAsync(long blockStart, CancellationToken cancellationToken)
    {
        if (_background) return await _store.AcquireFillAsync(_identity, blockStart, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CacheIoTimeout);
        try { return await _store.AcquireFillAsync(_identity, blockStart, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Another reader is still filling this block; serve it from the source and move on.
            _statistics?.Fallback(timeout: true);
            RecordSkip("fill_admission_timeout", abandonsResponse: false);
            return null;
        }
    }

    private async Task<T> CacheIoAsync<T>(bool write, string phase, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        // NAS syscalls can block synchronously and ignore cancellation. Admission
        // bounds both these operations and their buffers; never recycle either
        // until a detached operation has really stopped touching the memory.
        var queued = Stopwatch.GetTimestamp();
        var io = Task.Run(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            RecordPhase("io_start_delay", queued);
            if (BeforeCacheIo is not null) await BeforeCacheIo(write, cancellationToken).ConfigureAwait(false);
            var result = await operation(cancellationToken).ConfigureAwait(false);
            RecordPhase(phase, started);
            return result;
        }, CancellationToken.None);
        try
        {
            return _background
                ? await io.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await io.WaitAsync(CacheIoTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // The detached operation keeps the buffer and its slot until it really ends; this
            // stream takes a fresh slot for its next block. A timeout costs only the current block.
            var buffer = _buffer;
            var admission = _bufferAdmission;
            _buffer = null;
            _bufferAdmission = null;
            StopServing();
            _bufferStart = -1;
            _bufferCount = 0;
            _bufferVerified = false;
            _bufferFromCache = false;
            _ = ReleaseAfterIoAsync(io, buffer, admission);
            if (exception is not TimeoutException)
            {
                _bypassFill = true;
                throw;
            }
            _statistics?.Fallback(timeout: true);
            RecordSkip($"{phase}_timeout", abandonsResponse: false);
            throw new CacheIoTimeoutException(phase, exception);
        }
    }

    private static void RecordPhase(string phase, long started) =>
        PrometheusMetrics.Current?.RecordNativeCachePhase(phase, Stopwatch.GetElapsedTime(started));

    /// <summary>
    /// Counts every uncached block by reason and, once per response, tells operators when the
    /// rest of a foreground response will stream without being cached.
    /// </summary>
    private void RecordSkip(string reason, bool abandonsResponse)
    {
        PrometheusMetrics.Current?.RecordNativeCacheSkip(reason);
        _uncached.Cause = BackfillMissReasons.FromSkip(reason);
        if (!abandonsResponse || _background || _abandonLogged) return;
        _abandonLogged = true;
        Log.Warning("Native cache stopped caching {Name} at byte {Offset} for the rest of this response. Reason: {Reason}",
            _identity.DisplayName ?? _identity.ItemId, _position, reason);
    }

    /// <summary>A cache operation outlived the foreground deadline; it continues detached.</summary>
    private sealed class CacheIoTimeoutException(string phase, Exception inner)
        : TimeoutException($"Native cache {phase} exceeded its deadline.", inner);

    private static async Task ReleaseAfterIoAsync(Task io, byte[]? buffer, NativeBufferSlots.Lease? admission)
    {
        try { await io.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* Request already fell back or cancelled. */ }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            admission?.Dispose();
        }
    }

    private async ValueTask<int> ReadSourceRangeAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        _sourceWindowEnd = -1;
        _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
        if (_source.Position != _position) _source.Position = _position;
        var start = _position;
        var count = await _source.ReadAsync(destination[..(int)Math.Min(destination.Length, Length - _position)], cancellationToken).ConfigureAwait(false);
        _statistics?.SourceBytes(count);
        _uncached.Note(start, start + count);
        for (var block = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
            block < start + count; block += NativeCacheStore.BlockSize)
            if (block != _lastDirectMissBlock) { _statistics?.Miss(); _lastDirectMissBlock = block; }
        KeepResponseGeneration(); // These bytes came from the source, so they stay valid.
        _position += count;
        _servedBytes |= count > 0;
        _uncached.Served(count);
        LastReadCacheable = false;
        return count;
    }

    /// <summary>
    /// False when the revision changed before any byte was served (for example lazy RAR
    /// resolution persisting its blob during this first read): the response then continues
    /// from the source only, as at the start of <see cref="ReadCoreAsync"/>, and buffered
    /// block bytes must be re-read. Once bytes were served a change fails the response.
    /// </summary>
    private bool KeepResponseGeneration()
    {
        if (_untrackedSource || _generationIsCurrent()) return true;
        if (_servedBytes)
            throw new MediaSourceChangedException("Media source changed during this response. Retry the range against the current source.");
        _untrackedSource = true;
        _uncached.Cause = BackfillMissReasons.SourceChanged;
        _bypassFill = true;
        _bufferStart = -1;
        _activeFill?.Dispose();
        _activeFill = null;
        return false;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        LastReadCacheable = false;
        if (position != _position) _responseEnd = null;
        return _position = position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _source?.Dispose(); }
            finally { Release(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                try { if (_source is not null) await _source.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try
                    {
                        if (_pendingWrite is { } pending)
                        {
                            try { await pending.WaitAsync(CacheIoTimeout).ConfigureAwait(false); }
                            catch (TimeoutException) { }
                        }
                        // The response is over, so a short wait costs playback nothing and lets the
                        // next reader hit this response's blocks; a slow NAS keeps committing behind it.
                        if (_queuedCommits.Count > 0)
                        {
                            try { await Task.WhenAll(_queuedCommits).WaitAsync(CacheIoTimeout).ConfigureAwait(false); }
                            catch (TimeoutException) { }
                        }
                    }
                    finally { Release(); }
                }
            }
        }
        finally { await base.DisposeAsync().ConfigureAwait(false); }
    }

    private void Release()
    {
        // A waiting reader may be reclaiming this idle buffer right now; let it finish first.
        lock (_holdGate) _idle = false;
        _bufferAdmission?.MarkBusy();
        try { ReleaseOwned(); }
        finally { _owned?.Dispose(); }
    }

    private void ReleaseOwned()
    {
        _uncached.Flush();
        _activeFill?.Dispose();
        _activeFill = null;
        StopServing();
        if (_pendingWrite is { IsCompleted: false } pending)
        {
            _ = ReleaseAfterIoAsync(pending, _buffer, _bufferAdmission);
            _bufferAdmission = null;
        }
        else if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        _lease.Dispose();
        _transfer?.Dispose();
        _bufferAdmission?.Dispose();
    }
}
