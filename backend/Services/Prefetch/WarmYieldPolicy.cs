namespace NzbWebDAV.Services.Prefetch;

/// <summary>
/// Decides when a whole-file warm hands its slot to a more urgent queued job: only while every
/// reader of the file has a comfortable cached lead, so yielding never puts its own playback at
/// risk. The warm keeps its verified coverage and resumes once a slot frees up.
/// </summary>
internal sealed class WarmYieldPolicy(TimeSpan minimumLead, TimeSpan checkInterval)
{
    public static readonly TimeSpan DefaultMinimumLead = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(5);
    /// <summary>Playback rate assumed when the media duration is unknown: a high remux bitrate, so the lead is underestimated.</summary>
    internal const double FallbackBytesPerSecond = 100_000_000 / 8.0;

    private DateTimeOffset _nextCheck;
    // Players buffer in bursts and drop out of the active-read snapshot between them, so the last
    // positions seen stay in use, aged by the time since they were seen.
    private long[] _offsets = [];
    private DateTimeOffset _seenAt;

    public TimeSpan MinimumLead => minimumLead;

    public bool CheckDue(DateTimeOffset now)
    {
        if (now < _nextCheck) return false;
        _nextCheck = now + checkInterval;
        return true;
    }

    public void Observe(IReadOnlyCollection<long> readerOffsets, DateTimeOffset now)
    {
        if (readerOffsets.Count == 0) return;
        _offsets = [.. readerOffsets];
        _seenAt = now;
    }

    /// <summary>
    /// Playback time cached ahead of the slowest-served reader; <see cref="TimeSpan.MaxValue"/> when nobody
    /// reads the file, and null when it serves playback whose position has not been seen yet.
    /// </summary>
    /// <param name="cachedFrom">Contiguous cached bytes starting at an offset.</param>
    public async ValueTask<TimeSpan?> CachedLeadAsync(DateTimeOffset now, bool servesPlayback, double bytesPerSecond,
        Func<long, CancellationToken, ValueTask<long>> cachedFrom, CancellationToken ct)
    {
        if (_offsets.Length == 0) return servesPlayback ? null : TimeSpan.MaxValue;
        var lead = TimeSpan.MaxValue;
        foreach (var offset in _offsets)
        {
            var seconds = await cachedFrom(offset, ct).ConfigureAwait(false) / bytesPerSecond;
            var reader = TimeSpan.FromSeconds(seconds) - (now - _seenAt);
            if (reader < lead) lead = reader;
        }
        return lead;
    }

    public bool ShouldYield(TimeSpan? lead) => lead >= minimumLead;

    /// <summary>Average playback rate of the file, or <see cref="FallbackBytesPerSecond"/> when its duration is unknown.</summary>
    public static double PlaybackBytesPerSecond(long fileSize, long? mediaDurationMs)
        => mediaDurationMs is > 0 && fileSize > 0 ? fileSize / (mediaDurationMs.Value / 1000.0) : FallbackBytesPerSecond;

    /// <summary>Owners that mean someone is watching the file now: its warm must not yield without seeing their position.</summary>
    public static bool ServesPlayback(IEnumerable<string> owners) => owners.Any(owner => owner.Split(':') switch
    {
        [PrefetchRuntime.BackfillOwner] => true,
        ["read", ..] => true,
        ["plex", _, "realtime", ..] => true,
        _ => false,
    });
}
