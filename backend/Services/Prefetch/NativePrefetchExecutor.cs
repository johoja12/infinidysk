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
    PrefetchJobStore jobs, ActiveReadRegistry activeReads, PlexPlaybackRegistry? playback = null,
    Func<PrefetchSettings>? settingsProvider = null) : IPrefetchExecutor
{
    private PrefetchSettings Settings() => settingsProvider?.Invoke()
        ?? PrefetchSettings.Parse(config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings));
    public async Task ExecuteAsync(PrefetchJob job, CancellationToken ct)
    {
        var settings = Settings();
        if (native.Store is null) throw new PrefetchDeferredException("Native cache must be active before warming.");
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var item = await database.GetFileById(job.ItemId.ToString()).ConfigureAwait(false)
            ?? throw new ArgumentException("The imported media file no longer exists.");
        if (item.FileSize is not > 0 || item.FileSize > settings.MaxBytesPerItem)
            throw new ArgumentException("The media length is unavailable or exceeds the per-file warming cap.");
        var factory = scope.ServiceProvider.GetRequiredService<IDavContentStreamFactory>();
        await using var wireBudget = new PrefetchWireBudget(jobs, () => Settings().DailyByteBudget, ct);
        using var attribution = wireBudget.Enter();
        try
        {
        await using var readScope = BaseStoreStreamFile.BeginReadScope(config, scope.ServiceProvider,
            SemaphorePriority.Low, settings.ConnectionsPerJob, wireBudget.Token);
        Stream admitted;
        try { admitted = await native.WrapAsync(item, token => factory.OpenAsync(item, token), wireBudget.Token, requireNative: true).ConfigureAwait(false); }
        catch (InvalidOperationException) { throw new PrefetchDeferredException("Native cache buffers or metadata are unavailable."); }
        await using var stream = admitted;
        var cached = (NativeCachedStream)stream;
        await WarmAsync(native.Store, cached, job.Start, job.Length, async _ =>
        {
            var current = Settings();
            return !jobs.Paused && jobs.IsRunning(job.Id)
                && item.FileSize <= current.MaxBytesPerItem
                && (!current.PauseDuringPlayback || activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true)
                && await wireBudget.PrepareReadAsync(ct).ConfigureAwait(false);
        }, bytes => jobs.Progress(job.Id, cached.Identity.Generation, bytes), wireBudget.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && (wireBudget.Exceeded || jobs.WireBudgetBlocked) && !ct.IsCancellationRequested)
        {
            throw new PrefetchDeferredException(wireBudget.AccountingFailed || jobs.WireBudgetBlocked
                ? "Warming accounting failed. Repair local metadata storage and restart; playback remains available."
                : "Daily provider-payload warming budget exhausted; verified coverage retained.");
        }
    }
    public static Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, bool> spend, Action<long> progress, CancellationToken ct)
        => WarmAsync(store, stream, start, length, bytes => new ValueTask<bool>(spend(bytes)), progress, ct);

    public static async Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, ValueTask<bool>> spend, Action<long> progress, CancellationToken ct)
    {
        if (start < 0 || start >= stream.Length || length < 0 || length > stream.Length - start)
            throw new ArgumentException("The warm range is outside the media file.");
        var end = length == 0 ? stream.Length : start + length;
        var position = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        var alignedEnd = end % NativeCacheStore.BlockSize == 0 ? end
            : end + Math.Min(NativeCacheStore.BlockSize - end % NativeCacheStore.BlockSize, stream.Length - end);
        var missing = await store.GetMissingRangeBytesAsync(stream.Identity, position, alignedEnd, ct).ConfigureAwait(false);
        if (missing == 0)
        {
            if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before completion.");
            progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
            return;
        }
        using var reservation = await store.ReserveWarmAsync(stream.Identity, missing, ct).ConfigureAwait(false)
            ?? throw new PrefetchDeferredException("No writable folder has enough unreserved capacity for this media.");
        var probe = new byte[1];
        while (position < end)
        {
            position = await store.FindNextMissingOffsetAsync(stream.Identity, position, end, ct).ConfigureAwait(false);
            if (position >= end) break;
            ct.ThrowIfCancellationRequested();
            if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed; verified coverage must be rechecked.");
            var count = Math.Min(NativeCacheStore.BlockSize, stream.Length - position);
            if (!await spend(count).ConfigureAwait(false)) throw new PrefetchDeferredException("Daily warming budget exhausted or foreground playback has priority.");
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
