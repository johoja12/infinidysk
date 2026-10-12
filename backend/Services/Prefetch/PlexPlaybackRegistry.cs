using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Services.Prefetch;

public sealed class PlexPlaybackRegistry(TimeProvider clock)
{
    /// <summary>What verified playback means for warming one file.</summary>
    public enum Load
    {
        /// <summary>Nothing playing needs Usenet: no playback, or only fully cached files.</summary>
        None,
        /// <summary>The only playback that needs Usenet is this file itself.</summary>
        OnlyItem,
        /// <summary>Another file needs Usenet, or a playing session could not be resolved.</summary>
        Other,
    }

    private sealed record Resolution(HashSet<Guid> Streaming, HashSet<Guid> Cached);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _expiry = new(StringComparer.Ordinal);
    // What each server's playing sessions resolved to; absent while any is unresolved.
    private readonly Dictionary<string, Resolution> _items = new(StringComparer.Ordinal);
    public bool HasActivePlayback
    {
        get { lock (_gate) return _expiry.Values.Any(expiry => expiry > clock.GetUtcNow()); }
    }

    public Load LoadFor(Guid itemId)
    {
        lock (_gate)
        {
            var load = Load.None;
            foreach (var server in ActiveServers())
            {
                if (!_items.TryGetValue(server, out var resolution)) return Load.Other;
                foreach (var streaming in resolution.Streaming)
                {
                    if (streaming != itemId) return Load.Other;
                    load = Load.OnlyItem;
                }
            }
            return load;
        }
    }

    /// <summary>True when a verified playing session is reading this fully cached file.</summary>
    public bool IsCachedPlayback(Guid itemId)
    {
        lock (_gate)
            return ActiveServers().Any(server => _items.TryGetValue(server, out var resolution)
                && resolution.Cached.Contains(itemId));
    }

    /// <summary>
    /// Records the imported files the server's playing sessions resolved to, split by whether they
    /// still need Usenet; null when any playing session could not be resolved.
    /// </summary>
    public void RecordItems(string serverId, IReadOnlyCollection<Guid>? streaming, IReadOnlyCollection<Guid>? cached = null)
    {
        lock (_gate)
        {
            if (streaming is null || !_expiry.ContainsKey(serverId)) _items.Remove(serverId);
            else _items[serverId] = new([.. streaming], [.. cached ?? []]);
        }
    }

    public void Record(string serverId, IReadOnlyList<PlexSession> sessions, TimeSpan lifetime)
    {
        lock (_gate)
        {
            _items.Remove(serverId);
            if (!sessions.Any(session => session.State == "playing") || lifetime <= TimeSpan.Zero)
            { _expiry.Remove(serverId); return; }
            if (_expiry.Count >= 32 && !_expiry.ContainsKey(serverId))
            {
                var oldest = _expiry.MinBy(pair => pair.Value).Key;
                _expiry.Remove(oldest);
                _items.Remove(oldest);
            }
            _expiry[serverId] = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(lifetime.TotalSeconds, 300));
        }
    }
    public void RetainServers(IReadOnlyCollection<string> enabled)
    {
        lock (_gate)
            foreach (var id in _expiry.Keys.Where(id => !enabled.Contains(id, StringComparer.Ordinal)).ToArray())
            {
                _expiry.Remove(id);
                _items.Remove(id);
            }
    }

    private IEnumerable<string> ActiveServers()
    {
        var now = clock.GetUtcNow();
        return _expiry.Where(pair => pair.Value > now).Select(pair => pair.Key);
    }
}
