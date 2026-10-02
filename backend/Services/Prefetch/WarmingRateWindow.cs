namespace NzbWebDAV.Services.Prefetch;

/// <summary>Live speed of one running job: bytes per second over the recent window, and its last progress time.</summary>
public sealed record WarmingRate(double? RecentBytesPerSecond, DateTimeOffset LastProgressAt, bool Stalled);

/// <summary>
/// In-memory recent-rate window for running warming jobs. Each progress report adds a sample of the
/// bytes the job fetched since its previous report; the recent rate is the sum over the last
/// <see cref="Window"/> divided by the elapsed part of that window. Nothing is persisted: a restart
/// or a job leaving the running state simply drops its samples.
/// </summary>
public sealed class WarmingRateWindow
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    /// <summary>A running job with no progress report (fetch or verification) for this long reads as stalled.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(60);
    private const int MaxJobs = 64;
    private const int MaxSamplesPerJob = 512;

    private sealed class Track(DateTimeOffset started)
    {
        public DateTimeOffset Started { get; } = started;
        public DateTimeOffset LastProgress { get; set; } = started;
        public Queue<(DateTimeOffset At, long Bytes)> Samples { get; } = new();
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    /// <summary>Starts (or restarts) a job's window when it begins running.</summary>
    public void Start(string jobId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_tracks.Count >= MaxJobs && !_tracks.ContainsKey(jobId))
                _tracks.Remove(_tracks.MinBy(pair => pair.Value.LastProgress).Key);
            _tracks[jobId] = new Track(now);
        }
    }

    /// <summary>Records a progress report; <paramref name="warmedBytes"/> may be 0 while verifying cached blocks.</summary>
    public void Record(string jobId, long warmedBytes, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(jobId, out var track))
            {
                Start(jobId, now);
                track = _tracks[jobId];
            }
            track.LastProgress = now;
            if (warmedBytes > 0) track.Samples.Enqueue((now, warmedBytes));
            Trim(track, now);
        }
    }

    public void Stop(string jobId)
    {
        lock (_gate) _tracks.Remove(jobId);
    }

    public WarmingRate? Get(string jobId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(jobId, out var track)) return null;
            Trim(track, now);
            var span = now - (track.Started > now - Window ? track.Started : now - Window);
            double? rate = span < TimeSpan.FromSeconds(1) ? null
                : track.Samples.Sum(sample => sample.Bytes) / span.TotalSeconds;
            return new WarmingRate(rate, track.LastProgress, now - track.LastProgress >= StallAfter);
        }
    }

    private static void Trim(Track track, DateTimeOffset now)
    {
        while (track.Samples.Count > 0 && (track.Samples.Peek().At < now - Window || track.Samples.Count > MaxSamplesPerJob))
            track.Samples.Dequeue();
    }
}
