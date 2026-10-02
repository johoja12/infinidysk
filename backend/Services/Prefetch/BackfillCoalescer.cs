namespace NzbWebDAV.Services.Prefetch;

/// <summary>
/// Debounces backfill requests per item. A playback session can skip caching many separate
/// blocks (commit failures, admission or probe timeouts); each would otherwise become its own
/// warming job. Requests for one item collect until the item has been quiet for
/// <see cref="Quiet"/> (or <see cref="MaxDelay"/> passed since its first request), merging
/// ranges that lie within <see cref="MergeGap"/> of each other into a small bounded set.
/// </summary>
public sealed class BackfillCoalescer(TimeProvider clock)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(3);
    /// <summary>Ranges this close together warm as one; the cached blocks between them are skipped.</summary>
    public const long MergeGap = 64L * 1024 * 1024;
    public const int MaxRangesPerItem = 8;
    public const int MaxItems = 256;

    private sealed class Pending(DateTimeOffset first)
    {
        public DateTimeOffset First { get; } = first;
        public DateTimeOffset Last { get; set; } = first;
        public bool Forced { get; set; }
        public List<(long Start, long End)> Ranges { get; } = [];
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Pending> _pending = [];

    public int PendingItems { get { lock (_gate) return _pending.Count; } }

    public void Add(Guid itemId, long start, long length)
    {
        if (itemId == Guid.Empty || start < 0 || length <= 0 || start > long.MaxValue - length) return;
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_pending.TryGetValue(itemId, out var pending))
            {
                if (_pending.Count >= 2 * MaxItems) return;
                // Bounded memory: the oldest item is released at the next flush instead of growing the map.
                if (_pending.Count >= MaxItems && _pending.Values.Where(item => !item.Forced).MinBy(item => item.First) is { } oldest)
                    oldest.Forced = true;
                pending = new Pending(now);
                _pending[itemId] = pending;
            }
            pending.Last = now;
            Merge(pending.Ranges, start, start + length);
        }
    }

    /// <summary>Removes and returns items that are due, or every pending item when <paramref name="all"/>.</summary>
    public IReadOnlyList<(Guid ItemId, IReadOnlyList<(long Start, long Length)> Ranges)> TakeDue(bool all = false)
    {
        var now = clock.GetUtcNow();
        var due = new List<(Guid, IReadOnlyList<(long, long)>)>();
        lock (_gate)
        {
            foreach (var (itemId, pending) in _pending.ToArray())
            {
                if (!all && !pending.Forced && now - pending.Last < Quiet && now - pending.First < MaxDelay) continue;
                _pending.Remove(itemId);
                due.Add((itemId, pending.Ranges.Select(range => (range.Start, range.End - range.Start)).ToArray()));
            }
        }
        return due;
    }

    private static void Merge(List<(long Start, long End)> ranges, long start, long end)
    {
        for (var index = 0; index < ranges.Count;)
        {
            var range = ranges[index];
            if (range.Start <= end + MergeGap && start <= range.End + MergeGap)
            {
                start = Math.Min(start, range.Start);
                end = Math.Max(end, range.End);
                ranges.RemoveAt(index);
                index = 0; // The widened range may now reach a neighbour.
                continue;
            }
            index++;
        }
        ranges.Add((start, end));
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        // Keep a small bounded set: join the two ranges with the smallest gap between them.
        while (ranges.Count > MaxRangesPerItem)
        {
            var closest = 0;
            for (var index = 1; index < ranges.Count - 1; index++)
                if (ranges[index + 1].Start - ranges[index].End < ranges[closest + 1].Start - ranges[closest].End) closest = index;
            ranges[closest] = (ranges[closest].Start, Math.Max(ranges[closest].End, ranges[closest + 1].End));
            ranges.RemoveAt(closest + 1);
        }
    }
}
