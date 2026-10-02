namespace NzbWebDAV.Services.Prefetch;

/// <summary>Who was playing a file, as Plex reported it, for labelling the backfill jobs it causes.</summary>
public sealed record PlaybackViewer(string? User, string? Player, long DurationMs);

/// <summary>
/// Recently seen Plex playback per imported item. Backfill is flushed up to a few minutes after the
/// read that caused it, so entries outlive the session for <see cref="Retention"/>.
/// </summary>
public sealed class PlaybackViewerRegistry(TimeProvider clock)
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    public const int MaxItems = 256;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (PlaybackViewer Viewer, DateTimeOffset Seen)> _items = [];

    public void Note(Guid itemId, PlaybackViewer viewer)
    {
        if (itemId == Guid.Empty) return;
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_items.Count >= MaxItems && !_items.ContainsKey(itemId))
                _items.Remove(_items.MinBy(pair => pair.Value.Seen).Key);
            _items[itemId] = (viewer, now);
        }
    }

    public PlaybackViewer? Find(Guid itemId)
    {
        lock (_gate)
            return _items.TryGetValue(itemId, out var entry) && clock.GetUtcNow() - entry.Seen <= Retention ? entry.Viewer : null;
    }
}
