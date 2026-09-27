using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class CombinedStream(
    IEnumerable<Task<Stream>> streams,
    bool prefetchNextPart = false,
    long? prefetchReadBudget = null) : FastReadOnlyNonSeekableStream, ICacheReadEvidence
{
    private const int PrefixBytes = 64 * 1024;
    private const long MaximumPrefetchLead = 50L * 1024 * 1024;
    private readonly IEnumerator<Task<Stream>> _streams = streams.GetEnumerator();
    private Stream? _currentStream;
    private Task<PrefetchedPart>? _prefetchedPart;
    private CancellationTokenSource? _prefetchCancellation;
    private PrefetchedPart? _prefetchedPrefix;
    private int _prefixPosition;
    private bool _noNextPart;
    private long _position;
    private bool _isDisposed;
    public bool LastReadCacheable { get; private set; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        LastReadCacheable = false;
        if (buffer.Length == 0) return 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_prefetchedPrefix is { } prefix && _prefixPosition < prefix.Count)
            {
                var count = Math.Min(buffer.Length, prefix.Count - _prefixPosition);
                prefix.Buffer.AsMemory(_prefixPosition, count).CopyTo(buffer);
                _prefixPosition += count;
                _position += count;
                LastReadCacheable = prefix.Cacheable;
                if (_prefixPosition == prefix.Count) _prefetchedPrefix = null;
                return count;
            }

            if (_currentStream == null)
            {
                if (!await OpenNextAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (_prefetchedPrefix is not null) continue;
            }

            // read from our current stream
            var readCount = await _currentStream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += readCount;
            if (readCount > 0)
            {
                LastReadCacheable = _currentStream is ICacheReadEvidence { LastReadCacheable: true };
                TryPrefetchNext(cancellationToken);
                return readCount;
            }

            // If we couldn't read anything from our current stream,
            // it's time to advance to the next stream.
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }
    }

    private async Task<bool> OpenNextAsync(CancellationToken cancellationToken)
    {
        if (_prefetchedPart is { } pending)
        {
            var part = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            _prefetchedPart = null;
            _prefetchCancellation?.Dispose();
            _prefetchCancellation = null;
            _currentStream = part.Stream;
            _prefetchedPrefix = part.Count > 0 ? part : null;
            _prefixPosition = 0;
            return true;
        }

        if (_noNextPart || !_streams.MoveNext()) return false;
        _currentStream = await _streams.Current.ConfigureAwait(false);
        return true;
    }

    private void TryPrefetchNext(CancellationToken cancellationToken)
    {
        if (!prefetchNextPart || _prefetchedPart is not null || _noNextPart ||
            _currentStream is not PaddedLengthStream part)
            return;

        var remaining = part.Length - part.Position;
        // Start the next volume once this one has delivered a first chunk. A
        // provider can take many seconds to open a cold volume; waiting until
        // the final few MiB still leaves that latency on the response path.
        // One 64 KiB prefix and the existing NNTP permits bound the overlap.
        var lead = Math.Min(MaximumPrefetchLead, part.Length);
        if (remaining > lead || remaining <= 0 ||
            prefetchReadBudget is { } budget && _position + remaining + PrefixBytes >= budget)
            return;

        if (!_streams.MoveNext())
        {
            _noNextPart = true;
            return;
        }

        _prefetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _prefetchedPart = ReadNextPrefixAsync(_streams.Current, _prefetchCancellation.Token);
    }

    private static async Task<PrefetchedPart> ReadNextPrefixAsync(Task<Stream> open, CancellationToken cancellationToken)
    {
        Stream? next = null;
        try
        {
            next = await open.ConfigureAwait(false);
            var prefix = new byte[PrefixBytes];
            var count = await next.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
            return new PrefetchedPart(next, prefix, count,
                next is ICacheReadEvidence { LastReadCacheable: true });
        }
        catch
        {
            if (next is not null) await next.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed record PrefetchedPart(Stream Stream, byte[] Buffer, int Count, bool Cacheable);

    public override void Flush()
    {
        _currentStream?.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _currentStream?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed && disposing)
        {
            _prefetchCancellation?.Cancel();
            if (_prefetchedPart is { } pending)
                _ = DisposePrefetchedAsync(pending);
            _streams.Dispose();
            _currentStream?.Dispose();
            _prefetchCancellation?.Dispose();
            _isDisposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        if (_prefetchCancellation is { } cancellation)
            await cancellation.CancelAsync().ConfigureAwait(false);
        if (_prefetchedPart is { } pending)
            await DisposePrefetchedAsync(pending).ConfigureAwait(false);
        if (_currentStream != null) await _currentStream.DisposeAsync().ConfigureAwait(false);
        _streams.Dispose();
        _prefetchCancellation?.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task DisposePrefetchedAsync(Task<PrefetchedPart> pending)
    {
        try { await (await pending.ConfigureAwait(false)).Stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A speculative read may be cancelled after the response stops.
        }
    }
}
