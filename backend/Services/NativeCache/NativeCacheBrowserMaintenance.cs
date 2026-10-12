using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>Flushes bounded traffic deltas and backfills legacy display names off the playback path.</summary>
public sealed class NativeCacheBrowserMaintenance(NativeCacheService native, IServiceScopeFactory scopes) : BackgroundService
{
    /// <summary>Ticks (30 s each) between checks for protected copies whose item is gone; about 10 minutes.</summary>
    private const int ProtectionReviewTicks = 20;
    /// <summary>Items with several cached copies reviewed per tick.</summary>
    internal const int SupersededItemsPerTick = 50;
    /// <summary>Cached items checked per tick for removal from the library: about 30,000 items an hour.</summary>
    internal const int RemovedItemsPerTick = 250;
    /// <summary>A copy read this recently is kept even when its item is missing, covering imports that commit after the first read.</summary>
    internal static readonly TimeSpan RemovedItemGrace = TimeSpan.FromHours(1);
    private int _ticks;
    private string? _supersededCursor;
    private string? _removedCursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // Shutdown is bounded: a stalled NAS must not hold the host, while the
            // catalogue itself is local and ordinarily completes promptly.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await FlushAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { Log.Warning("Native cache traffic flush on shutdown failed ({Reason})", exception.GetType().Name); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!await native.WaitForInitializationAsync(ct).ConfigureAwait(false) || native.Store is not { } store) return;
        await FlushAsync(ct).ConfigureAwait(false);
        try
        {
            await store.PruneBrowserHistoryAsync(ct).ConfigureAwait(false);
            var ids = await store.GetUnnamedItemIdsAsync(100, ct).ConfigureAwait(false);
            var parsed = ids.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(id => id != Guid.Empty).ToArray();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parsed.Length > 0)
            {
                using var scope = scopes.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                var items = await database.GetItemsByIdsBatchedAsync(parsed, ct: ct).ConfigureAwait(false);
                foreach (var item in items) names[item.Id.ToString("N")] = item.Name;
            }
            foreach (var id in ids) names.TryAdd(id, id);
            await store.UpdateDisplayNamesAsync(names, ct).ConfigureAwait(false);
            if (++_ticks % ProtectionReviewTicks == 1) await ReleaseStaleProtectionAsync(store, ct).ConfigureAwait(false);
            await EvictSupersededCopiesAsync(store, ct).ConfigureAwait(false);
            await EvictRemovedItemCopiesAsync(store, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        { Log.Warning("Native cache browser maintenance failed ({Reason})", exception.GetType().Name); }
    }

    /// <summary>
    /// Health checks protect a cached copy that Usenet no longer has. Once its item is deleted, or the
    /// item now points at a different revision, the copy returns to normal eviction.
    /// </summary>
    internal async Task<int> ReleaseStaleProtectionAsync(NativeCacheStore store, CancellationToken ct)
    {
        var protectedEntries = await store.ListProtectedAsync(500, ct).ConfigureAwait(false);
        if (protectedEntries.Count == 0) return 0;
        var ids = protectedEntries.Select(entry => Guid.TryParse(entry.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var items = (await database.GetItemsByIdsBatchedAsync(ids, ct: ct).ConfigureAwait(false))
            .ToDictionary(item => item.Id.ToString("N"), StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        foreach (var entry in protectedEntries)
        {
            var key = Guid.TryParse(entry.ItemId, out var parsed) ? parsed.ToString("N") : entry.ItemId;
            if (!items.TryGetValue(key, out var item)) { stale.Add(entry.Key); continue; }
            var current = await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false);
            if (current is not null && current.Key != entry.Key) stale.Add(entry.Key);
        }
        if (stale.Count > 0)
        {
            await store.ReleaseProtectionAsync(stale, ct).ConfigureAwait(false);
            Log.Information("Released eviction protection for {Count} cached copies whose files were removed or replaced.", stale.Count);
        }
        return stale.Count;
    }

    /// <summary>
    /// A file whose source or cache identity changed keeps its earlier cached copies until age eviction
    /// reaches them, though nothing reads them again. Reviews the next batch of items with several copies
    /// and evicts every copy but the current one, including every copy of an item whose current revision
    /// is not cached at all, since reads only ever open the current identity. Pinned copies, copies being
    /// read, items that no longer exist and items whose current identity cannot be resolved are left alone.
    /// </summary>
    internal async Task<(int Copies, long Bytes)> EvictSupersededCopiesAsync(NativeCacheStore store, CancellationToken ct)
    {
        var copies = await store.ListItemsWithSeveralCopiesAsync(_supersededCursor, SupersededItemsPerTick, ct).ConfigureAwait(false);
        _supersededCursor = copies.Count == 0 ? null : copies[^1].ItemId;
        if (copies.Count == 0) return (0, 0);
        var ids = copies.Select(copy => Guid.TryParse(copy.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var items = (await database.GetItemsByIdsBatchedAsync(ids, ct: ct).ConfigureAwait(false))
            .ToDictionary(item => item.Id.ToString("N"), StringComparer.OrdinalIgnoreCase);
        var evicted = 0;
        var bytes = 0L;
        foreach (var group in copies.GroupBy(copy => copy.ItemId, StringComparer.Ordinal))
        {
            var key = Guid.TryParse(group.Key, out var parsed) ? parsed.ToString("N") : group.Key;
            if (!items.TryGetValue(key, out var item)) continue;
            var current = await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false);
            if (current is null) continue;
            foreach (var copy in group.Where(copy => copy.Key != current.Key && !copy.Pinned))
            {
                if (await store.EvictKeyAsync(copy.Folder, copy.Key, ct).ConfigureAwait(false) != NativeCacheEntryEviction.Evicted) continue;
                evicted++;
                bytes += copy.Bytes;
            }
        }
        if (evicted > 0)
            Log.Information("Removed {Count} superseded cached copies ({Bytes:F1} GiB) of files whose cache identity changed.",
                evicted, bytes / (1024.0 * 1024 * 1024));
        return (evicted, bytes);
    }

    /// <summary>
    /// Nothing reads the cached copies of a file once it leaves the library. Reviews the next batch of cached
    /// items and evicts every copy of an item that no longer exists. Pinned copies, copies being read and copies
    /// read within <see cref="RemovedItemGrace"/> are left alone.
    /// </summary>
    internal async Task<(int Copies, long Bytes)> EvictRemovedItemCopiesAsync(NativeCacheStore store, DateTimeOffset now, CancellationToken ct)
    {
        var copies = await store.ListItemCopiesAsync(_removedCursor, RemovedItemsPerTick, ct).ConfigureAwait(false);
        _removedCursor = copies.Count == 0 ? null : copies[^1].ItemId;
        if (copies.Count == 0) return (0, 0);
        var ids = copies.Select(copy => Guid.TryParse(copy.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return (0, 0);
        HashSet<string> live;
        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            live = (await database.GetItemsByIdsBatchedAsync(ids, ct: ct).ConfigureAwait(false))
                .Select(item => item.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var evicted = 0;
        var bytes = 0L;
        foreach (var copy in copies)
        {
            if (!Guid.TryParse(copy.ItemId, out var parsed) || live.Contains(parsed.ToString("N"))) continue;
            if (copy.Pinned || now - copy.LastAccess < RemovedItemGrace) continue;
            if (await store.EvictKeyAsync(copy.Folder, copy.Key, ct).ConfigureAwait(false) != NativeCacheEntryEviction.Evicted) continue;
            evicted++;
            bytes += copy.Bytes;
        }
        if (evicted > 0)
            Log.Information("Removed {Count} cached copies ({Bytes:F1} GiB) of files no longer in the library.",
                evicted, bytes / (1024.0 * 1024 * 1024));
        return (evicted, bytes);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (native.Store is not { } store) return;
        var rows = native.Statistics.DrainTraffic();
        try { await store.SaveTrafficAsync(rows, ct).ConfigureAwait(false); }
        catch
        {
            native.Statistics.RestoreTraffic(rows);
            throw;
        }
    }
}
