using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Services.Plex;

public sealed record PlexSnapshot<T>(IReadOnlyList<T> Data, DateTimeOffset? LastSuccess, bool IsStale, string? Error);

/// <summary>Coalesced, bounded snapshots; failed refreshes preserve last-good data.</summary>
public sealed class PlexCatalogueService(PlexApiClient api, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);

    public Task<PlexSnapshot<PlexLibrary>> GetLibrariesAsync(PlexServer server, bool forceRefresh = false, CancellationToken ct = default) =>
        GetAsync(server, "libraries", forceRefresh, token => api.GetLibrariesAsync(server, token), ct);
    public Task<PlexSnapshot<PlexUser>> GetUsersAsync(PlexServer server, bool forceRefresh = false, CancellationToken ct = default) =>
        GetAsync(server, "users", forceRefresh, token => api.GetUsersAsync(server, token), ct);
    public Task<PlexSnapshot<PlexSource>> GetSourcesAsync(PlexServer server, string? libraryId, bool forceRefresh = false, CancellationToken ct = default) =>
        GetAsync(server, "sources:" + libraryId, forceRefresh, token => api.GetSourcesAsync(server, libraryId, token), ct);

    private async Task<PlexSnapshot<T>> GetAsync<T>(PlexServer server, string resource, bool force,
        Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, CancellationToken ct)
    {
        // Credentials and endpoint changes cannot reuse an earlier authorization snapshot.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{server.Id}\n{server.Url}\n{server.Token}\n{resource}")));
        Entry<T> entry;
        long version;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var value))
            {
                if (_entries.Count >= 128) _entries.Remove(_entries.Keys.First());
                _entries[key] = value = new Entry<T>();
            }
            entry = (Entry<T>)value;
            version = entry.Version;
        }
        await entry.Refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (entry.Snapshot is not null && (entry.Version != version || now < entry.RetryAfter ||
                (!force && now < entry.NextRefresh))) return entry.Snapshot;
            try
            {
                var data = await fetch(ct).ConfigureAwait(false);
                entry.Snapshot = new(data, clock.GetUtcNow(), false, null);
                entry.NextRefresh = clock.GetUtcNow().AddMinutes(1);
                entry.RetryAfter = DateTimeOffset.MinValue;
            }
            catch (PlexRequestException exception)
            {
                entry.Snapshot = new(entry.Snapshot?.Data ?? [], entry.Snapshot?.LastSuccess, true, exception.Message);
                entry.RetryAfter = clock.GetUtcNow().AddSeconds(30);
            }
            Interlocked.Increment(ref entry.Version);
            return entry.Snapshot;
        }
        finally { entry.Refresh.Release(); }
    }

    private sealed class Entry<T>
    {
        public SemaphoreSlim Refresh { get; } = new(1, 1);
        public PlexSnapshot<T>? Snapshot { get; set; }
        public DateTimeOffset NextRefresh { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
        public long Version;
    }
}
