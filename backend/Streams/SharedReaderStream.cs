using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.StreamTrace;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Per-reader seekable view of a <see cref="SharedStreamEntry"/> ring. Out-of-window
/// seeks and tail-pinning evictions detach to a private fallback at the exact cursor.
/// </summary>
internal sealed class SharedReaderStream : FastReadOnlyStream, IDeliveredBytesValidation
{
    private readonly SharedStreamEntry _entry;
    private readonly SharedStreamRingBuffer _ring;
    private readonly long _readerId;
    private readonly long _fileSize;
    private readonly long _ringSize;
    private readonly SharedStreamFallbackFactory _fallbackFactory;
    private long _cursor;
    private Stream? _fallback;
    private bool _detached;
    private long _ringDeliveredThrough = -1;
    private Exception? _deliveredFailure;
    private int _disposed;
    private string? _responseGeneration;
    private bool _servedBytes;
    private int _sourceRestarts;

    // A revision published before any byte reached the client (for example lazy RAR
    // resolution persisting its blob during the first read) cannot make this response
    // inconsistent, so it reopens against the current source a bounded number of times.
    internal const int MaxUnservedSourceRestarts = 3;

    internal SharedReaderStream(
        SharedStreamEntry entry,
        SharedStreamRingBuffer ring,
        long readerId,
        long cursor,
        long fileSize,
        long ringSize,
        SharedStreamFallbackFactory fallbackFactory)
    {
        _entry = entry;
        _ring = ring;
        _readerId = readerId;
        _cursor = cursor;
        _fileSize = fileSize;
        _ringSize = ringSize;
        _fallbackFactory = fallbackFactory;
        _responseGeneration = entry.GenerationIdentity;
    }

    internal long ReaderId => _readerId;
    internal SharedStreamEntry Entry => _entry;
    internal bool IsDetached => _detached;
    internal long Cursor => _cursor;

    public override bool CanSeek => true;
    public override long Length => _fileSize;

    public override long Position
    {
        get => _cursor;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) return 0;
        if (_deliveredFailure is { } delivered)
            throw DuplicateFailure(delivered);
        if (_cursor >= _fileSize) return 0;

        if (_detached)
            return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            if (!_entry.ValidateSourceGeneration())
                return await RestartOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);
            var result = _ring.TryCopyAt(_readerId, _cursor, buffer.Span);
            switch (result.Kind)
            {
                case RingReadKind.Copied when result.Count == 0:
                    // The ring completed before this reader reached the file end: the shared
                    // pump stopped (entry teardown or an upstream pipeline that ended early),
                    // which is not evidence of missing data. Continue on a private source so
                    // only a truncation that reproduces there surfaces as a short read.
                    if (!_entry.ValidateSourceGeneration())
                        return await RestartOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);
                    await DetachToPrivateAsync(cancellationToken).ConfigureAwait(false);
                    return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);

                case RingReadKind.Copied:
                    if (!_entry.ValidateSourceGeneration())
                        return await RestartOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);
                    _cursor += result.Count;
                    _servedBytes |= result.Count > 0;
                    if (result.Count > 0)
                    {
                        _ringDeliveredThrough = _cursor;
                        _entry.NotifyCursorAdvanced(_readerId, _cursor);
                    }

                    return result.Count;

                case RingReadKind.NeedWait:
                    {
                        var waitStarted = Stopwatch.GetTimestamp();
                        await _ring.WaitForDataAsync(_readerId, _cursor, cancellationToken)
                            .ConfigureAwait(false);
                        StreamTrace.TryStall(
                            MultiProviderNntpClient.CurrentStreamTraceRange,
                            StreamStallKind.ConsumerWait,
                            Stopwatch.GetElapsedTime(waitStarted));
                        continue;
                    }

                case RingReadKind.Evicted:
                case RingReadKind.Released:
                case RingReadKind.Detached:
                    await DetachToPrivateAsync(cancellationToken).ConfigureAwait(false);
                    return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);

                case RingReadKind.Failed when result.Exception is MediaSourceChangedException
                    && await TryRestartUnservedAsync(cancellationToken).ConfigureAwait(false):
                    return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);

                case RingReadKind.Failed:
                    _deliveredFailure = result.Exception;
                    DetachQuiet();
                    throw result.DispatchFailure();

                default:
                    throw new InvalidOperationException($"Unexpected ring read kind {result.Kind}.");
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        long target;
        try
        {
            target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_cursor + offset),
                SeekOrigin.End => checked(_fileSize + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (target < 0 || target > _fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        if (_detached)
        {
            _cursor = target;
            _fallback?.Seek(target, SeekOrigin.Begin);
            return _cursor;
        }

        var tail = _ring.TailStart;
        var frontier = _ring.Frontier;
        if (target == _fileSize || (target >= tail && target <= frontier + _ringSize))
        {
            _cursor = target;
            _entry.NotifyCursorAdvanced(_readerId, target);
            return _cursor;
        }

        DetachQuiet();
        _cursor = target;
        return _cursor;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DetachQuiet();
        var fallback = _fallback;
        _fallback = null;
        fallback?.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DetachQuiet();
        if (_fallback is { } fallback)
        {
            _fallback = null;
            await fallback.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private static Exception DuplicateFailure(Exception delivered)
    {
        ExceptionDispatchInfo.Capture(delivered).Throw();
        return delivered;
    }

    private void DetachQuiet()
    {
        if (_detached) return;
        _detached = true;
        _entry.Detach(_readerId);
    }

    async ValueTask IDeliveredBytesValidation.ValidateDeliveredAsync(CancellationToken cancellationToken)
    {
        await ValidateRingDeliveredAsync(cancellationToken).ConfigureAwait(false);
        await _fallback.ValidateDeliveredAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ValidateRingDeliveredAsync(CancellationToken cancellationToken)
    {
        if (_ringDeliveredThrough < 0) return;
        await _entry.ValidateThroughAsync(_ringDeliveredThrough, cancellationToken).ConfigureAwait(false);
        _ringDeliveredThrough = -1;
    }

    private async Task DetachToPrivateAsync(CancellationToken cancellationToken)
    {
        // The private fallback refetches from the cursor, so validate the shared fetch's bytes first.
        await ValidateRingDeliveredAsync(cancellationToken).ConfigureAwait(false);
        DetachQuiet();
        _fallback ??= await _fallbackFactory(_cursor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadFallbackAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_servedBytes && _responseGeneration is not null && !_entry.ValidateSourceGeneration()) ThrowGenerationChanged();
            _fallback ??= await _fallbackFactory(_cursor, cancellationToken).ConfigureAwait(false);
            var evidence = _fallback as IStreamGenerationEvidence;
            if (_servedBytes && _responseGeneration is not null && evidence?.GenerationIdentity != _responseGeneration)
                ThrowGenerationChanged();
            if (!_servedBytes) _responseGeneration = evidence?.GenerationIdentity;
            if (evidence?.IsSourceCurrent == false)
            {
                if (await TryRestartUnservedAsync(cancellationToken).ConfigureAwait(false)) continue;
                ThrowGenerationChanged();
            }
            var read = await _fallback.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (evidence?.IsSourceCurrent == false || _responseGeneration is not null && evidence?.GenerationIdentity != _responseGeneration)
            {
                if (await TryRestartUnservedAsync(cancellationToken).ConfigureAwait(false)) continue;
                ThrowGenerationChanged();
            }
            _cursor += read;
            _servedBytes |= read > 0;
            return read;
        }
    }

    private async ValueTask<int> RestartOrThrowAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await TryRestartUnservedAsync(cancellationToken).ConfigureAwait(false)) ThrowGenerationChanged();
        return await ReadFallbackAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryRestartUnservedAsync(CancellationToken cancellationToken)
    {
        if (_servedBytes || _sourceRestarts >= MaxUnservedSourceRestarts) return false;
        _sourceRestarts++;
        DetachQuiet();
        if (_fallback is { } stale)
        {
            _fallback = null;
            await stale.DisposeAsync().ConfigureAwait(false);
        }
        _responseGeneration = null;
        // Give an in-flight blob publication a moment to finish before reopening.
        await Task.Delay(TimeSpan.FromMilliseconds(50 * _sourceRestarts), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void ThrowGenerationChanged()
    {
        _deliveredFailure = new MediaSourceChangedException("Media source changed during this response. Retry the current source.");
        DetachQuiet();
        throw _deliveredFailure;
    }
}
