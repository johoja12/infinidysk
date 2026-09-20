using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Services.Prefetch;

public sealed class PrefetchDeferredException(string message, bool countsAsFailure = false) : Exception(message)
{
    public bool CountsAsFailure { get; } = countsAsFailure;
}

public sealed class NativePrefetchExecutor(IServiceScopeFactory scopes, NativeCacheService native, ConfigManager config,
    PrefetchJobStore jobs, ActiveReadRegistry activeReads, PlexPlaybackRegistry? playback = null) : IPrefetchExecutor
{
    public async Task ExecuteAsync(PrefetchJob job, CancellationToken ct)
    {
        var settings = PrefetchSettings.Parse(config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings));
        if (native.Store is null) throw new PrefetchDeferredException("Native cache must be active before warming.");
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var item = await database.GetFileById(job.ItemId.ToString()).ConfigureAwait(false)
            ?? throw new ArgumentException("The imported media file no longer exists.");
        if (item.FileSize is not > 0 || item.FileSize > settings.MaxBytesPerItem)
            throw new ArgumentException("The media length is unavailable or exceeds the per-file warming cap.");
        var factory = scope.ServiceProvider.GetRequiredService<IDavContentStreamFactory>();
        await using var readScope = BaseStoreStreamFile.BeginReadScope(config, scope.ServiceProvider, ct,
            SemaphorePriority.Low, settings.ConnectionsPerJob);
        Stream admitted;
        try { admitted = await native.WrapAsync(item, token => factory.OpenAsync(item, token), ct, requireNative: true).ConfigureAwait(false); }
        catch (InvalidOperationException) { throw new PrefetchDeferredException("Native cache buffers or metadata are unavailable."); }
        await using var stream = admitted;
        var cached = (NativeCachedStream)stream;
        await WarmAsync(native.Store, cached, job.Start, job.Length, bytes =>
        {
            var current = PrefetchSettings.Parse(config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings));
            return !jobs.Paused && jobs.IsRunning(job.Id)
                && (!current.PauseDuringPlayback || activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true)
                && jobs.TrySpendDailyBudget(bytes, current.DailyByteBudget);
        }, bytes => jobs.Progress(job.Id, cached.Identity.Generation, bytes), ct).ConfigureAwait(false);
    }
    public static async Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, bool> spend, Action<long> progress, CancellationToken ct)
    {
        if (start < 0 || start >= stream.Length || length < 0 || length > stream.Length - start)
            throw new ArgumentException("The warm range is outside the media file.");
        var end = length == 0 ? stream.Length : start + length;
        var position = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        var coverage = await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false);
        using var reservation = await store.ReserveWarmAsync(stream.Identity, stream.Length - coverage, ct).ConfigureAwait(false)
            ?? throw new PrefetchDeferredException("No writable folder has enough unreserved capacity for this media.");
        var probe = new byte[1];
        while (position < end)
        {
            position = await store.FindNextMissingOffsetAsync(stream.Identity, position, end, ct).ConfigureAwait(false);
            if (position >= end) break;
            ct.ThrowIfCancellationRequested();
            if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed; verified coverage must be rechecked.");
            var count = Math.Min(NativeCacheStore.BlockSize, stream.Length - position);
            if (!spend(count)) throw new PrefetchDeferredException("Daily warming budget exhausted or foreground playback has priority.");
            stream.Position = position;
            if (await stream.ReadAsync(probe, ct).ConfigureAwait(false) != 1 || !stream.LastReadCacheable || !stream.IsSourceCurrent
                || await store.FindNextMissingOffsetAsync(stream.Identity, position, Math.Min(position + count, end), ct).ConfigureAwait(false) == position)
                throw new PrefetchDeferredException("Source bytes were not verified or the cache could not commit this range.", countsAsFailure: true);
            progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
            position = Math.Min(position + count, end);
        }
        if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before completion.");
        progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
    }
}
