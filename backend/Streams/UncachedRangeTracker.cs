using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Observability;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Remembers what one foreground response served without caching, widened to whole integrity
/// blocks. Contiguous ranges merge and each finished range becomes one backfill request, so
/// nothing a player streamed stays uncached merely because the cache was busy. Short responses
/// (scanner probes) never request backfill (#44).
/// </summary>
internal sealed class UncachedRangeTracker(long length, Action<long, long>? backfill, string name,
    long minimumServedBytes = UncachedRangeTracker.PlaybackBytes)
{
    public const long PlaybackBytes = 4L * NativeCacheStore.BlockSize;
    private long _start = -1;
    private long _end = -1;
    private long _served;

    public void Served(long bytes) => _served += bytes;

    public void Note(long start, long end)
    {
        if (backfill is null || end <= start) return;
        var alignedStart = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        var alignedEnd = Math.Min(length, (end + NativeCacheStore.BlockSize - 1) / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize);
        if (_start >= 0 && alignedStart <= _end && alignedEnd >= _start)
        {
            _start = Math.Min(_start, alignedStart);
            _end = Math.Max(_end, alignedEnd);
            return;
        }
        Flush();
        _start = alignedStart;
        _end = alignedEnd;
    }

    public void Flush()
    {
        if (_start < 0) return;
        var (start, end) = (_start, _end);
        _start = _end = -1;
        if (_served < minimumServedBytes)
        {
            PrometheusMetrics.Current?.RecordNativeCacheSkip("backfill_short_read");
            return;
        }
        PrometheusMetrics.Current?.RecordNativeCacheSkip("backfill_scheduled");
        try { backfill!(start, end - start); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Log.Debug(exception, "Native cache backfill request failed for {Name}", name); }
    }
}
