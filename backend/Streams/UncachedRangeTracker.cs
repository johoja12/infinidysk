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
internal sealed class UncachedRangeTracker(long length, Action<long, long, string>? backfill, string name,
    long minimumServedBytes = UncachedRangeTracker.PlaybackBytes)
{
    public const long PlaybackBytes = 4L * NativeCacheStore.BlockSize;
    private long _start = -1;
    private long _end = -1;
    private string? _reason;
    private long _served;

    /// <summary>Why the stream is currently not caching (a <see cref="BackfillMissReasons"/> code); applies to later notes.</summary>
    public string? Cause { get; set; }

    public void Served(long bytes) => _served += bytes;

    public void Note(long start, long end)
    {
        if (backfill is null || end <= start) return;
        var cause = Cause ?? BackfillMissReasons.UncachedRead;
        var alignedStart = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        var alignedEnd = Math.Min(length, (end + NativeCacheStore.BlockSize - 1) / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize);
        if (_start >= 0 && alignedStart <= _end && alignedEnd >= _start)
        {
            _start = Math.Min(_start, alignedStart);
            _end = Math.Max(_end, alignedEnd);
            if (_reason == BackfillMissReasons.UncachedRead) _reason = cause;
            return;
        }
        Flush();
        _start = alignedStart;
        _end = alignedEnd;
        _reason = cause;
    }

    public void Flush()
    {
        if (_start < 0) return;
        var (start, end, reason) = (_start, _end, _reason ?? BackfillMissReasons.UncachedRead);
        _start = _end = -1;
        _reason = null;
        if (_served < minimumServedBytes)
        {
            PrometheusMetrics.Current?.RecordNativeCacheSkip("backfill_short_read");
            return;
        }
        PrometheusMetrics.Current?.RecordNativeCacheSkip("backfill_scheduled");
        try { backfill!(start, end - start, reason); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Log.Debug(exception, "Native cache backfill request failed for {Name}", name); }
    }
}

/// <summary>
/// Stable codes for why playback served bytes without caching them, recorded on the backfill job
/// that fills them in later. Derived from the Native Cache skip that preceded the uncached read.
/// </summary>
public static class BackfillMissReasons
{
    public const string BuffersFull = "buffers-full";
    public const string BlockBusy = "block-busy";
    public const string StorageSlow = "storage-slow";
    public const string WriteQueueFull = "write-queue-full";
    public const string WriteFailed = "write-failed";
    public const string SourceInterrupted = "source-interrupted";
    public const string SourceChanged = "source-changed";
    public const string UncachedRead = "uncached-read";

    public static string FromSkip(string skip) => skip switch
    {
        "admission_overflow" => BuffersFull,
        "fill_admission_timeout" => BlockBusy,
        "commit_queue_full" => WriteQueueFull,
        "commit_failed" or "commit_rejected" => WriteFailed,
        "fill_source_failure" => SourceInterrupted,
        _ when skip.EndsWith("_timeout", StringComparison.Ordinal) => StorageSlow,
        _ => UncachedRead,
    };
}
