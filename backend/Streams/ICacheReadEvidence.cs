using NzbWebDAV.Extensions;

namespace NzbWebDAV.Streams;

/// <summary>
/// Evidence for the bytes returned by the last successful asynchronous read.
/// Absence means unknown, never trusted. Implementations must exclude synthetic
/// padding and bytes whose integrity will only be checked on a subsequent read.
/// </summary>
public interface ICacheReadEvidence
{
    bool LastReadCacheable { get; }
}

internal sealed class NativeCacheReadContext : IDisposable
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private readonly Scope? _previous = Current.Value;
    public static bool IsActive => Current.Value is not null;
    public static long? ReadBudget => Current.Value?.ReadBudget;

    /// <param name="gaps">
    /// Where gap fills under this context are recorded; nested contexts inherit the outer recorder.
    /// Segment downloads keep the context they started under, so one recorder must span a stream's reads.
    /// </param>
    public NativeCacheReadContext(long readBudget = 4L * 1024 * 1024, NativeCacheGapRecorder? gaps = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readBudget);
        Current.Value = new Scope(readBudget, gaps ?? _previous?.Gaps);
    }

    /// <summary>Notes a segment the source replaced with a gap fill while reading for the native cache.</summary>
    public static void RecordGapFill(Exception cause) => Current.Value?.Gaps?.Record(cause);

    public void Dispose() => Current.Value = _previous;

    private sealed record Scope(long ReadBudget, NativeCacheGapRecorder? Gaps);
}

/// <summary>
/// First conclusive gap fill (an article missing on every provider, or persistently corrupt) seen
/// by one cache stream's source. Gap fills keep playback offsets aligned, so the read itself succeeds
/// and only this record tells warming that retrying cannot verify the bytes.
/// </summary>
internal sealed class NativeCacheGapRecorder
{
    private Exception? _conclusive;
    public Exception? Conclusive => Volatile.Read(ref _conclusive);

    public void Record(Exception cause)
    {
        if (!cause.IsInconclusiveArticleMiss())
            Interlocked.CompareExchange(ref _conclusive, cause, null);
    }
}
