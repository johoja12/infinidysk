using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using Serilog;

namespace NzbWebDAV.Services.Prefetch;

/// <summary>Optional feature lifetime: cache/index failures cannot prevent ordinary source streaming.</summary>
public sealed class PrefetchRuntime(ConfigManager config, NativeCacheService native, IServiceScopeFactory scopes,
    ActiveReadRegistry activeReads, PlexPlaybackRegistry? playback = null,
    IHostApplicationLifetime? applicationLifetime = null, TimeProvider? clock = null) : BackgroundService
{
    /// <summary>Owner of jobs that fill in playback a foreground stream could not cache.</summary>
    public const string BackfillOwner = "backfill";
    // Above manual warming (50): someone just watched these bytes.
    private const int BackfillPriority = 60;
    /// <summary>A completed backfill job this recent reopens for nearby misses of the same revision.</summary>
    public static readonly TimeSpan BackfillReviveWindow = TimeSpan.FromHours(6);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly BackfillCoalescer _backfill = new(clock ?? TimeProvider.System);
    /// <summary>Debounced backfill requests not yet written to the queue.</summary>
    public int PendingBackfillItems => _backfill.PendingItems;
    /// <summary>Recent Plex playback per item, attached to the backfill jobs that playback causes.</summary>
    public PlaybackViewerRegistry Viewers { get; } = new(clock ?? TimeProvider.System);
    private readonly Lock _gate = new();
    private PrefetchJobStore? _jobs;
    private PrefetchCoordinator? _coordinator;
    private bool _initialized;
    private sealed record SettingsSnapshot(string? Json, PrefetchSettings Value);
    private SettingsSnapshot? _settings;
    public string? InitializationError { get; private set; }
    private string? _runtimeError;
    public string? RuntimeError => Volatile.Read(ref _runtimeError) ?? _coordinator?.RuntimeError;
    public bool Healthy => InitializationError is null && RuntimeError is null;
    public void ReportMetadataFailure()
    {
        Interlocked.CompareExchange(ref _runtimeError, PrefetchCoordinator.MetadataFailureMessage, null);
        _coordinator?.ReportMetadataFailure();
    }
    public PrefetchJobStore? Jobs { get { Initialize(); return _jobs; } }
    public PrefetchCoordinator? Coordinator { get { Initialize(); return _coordinator; } }

    public async Task WaitForInitializationAsync(CancellationToken cancellationToken)
    {
        await native.WaitForInitializationAsync(cancellationToken).ConfigureAwait(false);
        Initialize();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            if (!Healthy || _initialized || native.Store is null || native.ActiveSettings is null) return;
            _initialized = true;
            try
            {
                _jobs = new PrefetchJobStore(Path.Combine(native.ActiveSettings.MetadataPath, "prefetch.db"), settings: Settings);
                native.BackfillSink = _backfill.Add;
                _coordinator = new PrefetchCoordinator(_jobs, new NativePrefetchExecutor(scopes, native, config, _jobs, activeReads, playback, Settings),
                    Settings, () => native.ActiveSettings.Folders.Any(folder => folder.Enabled && !folder.ReadOnly)
                        && (!Settings().PauseDuringPlayback || activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true),
                    applicationStopping: applicationLifetime?.ApplicationStopping ?? CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
            {
                _jobs?.Dispose(); _jobs = null;
                InitializationError = "Prefetch metadata could not initialize. Check local cache metadata storage and permissions.";
            }
        }
    }

    /// <summary>
    /// True when routine warming of these ranges would only re-verify cache that a completed warm of the
    /// item's current revision verified within the intent window. Manual requests do not consult this.
    /// </summary>
    public async Task<bool> IsRecentlyWarmAsync(DavItem item, IReadOnlyList<(long Start, long Length)> ranges, CancellationToken ct)
    {
        if (Jobs is not { } jobs || native.Store is not { } store || ranges.Count == 0) return false;
        try
        {
            var identity = await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false);
            if (identity is null) return false;
            var since = DateTimeOffset.UtcNow.AddHours(-Settings().IntentTtlHours);
            return await NativePrefetchExecutor.IsRecentlyWarmAsync(store, jobs, identity, item.Id, ranges, since, ct).ConfigureAwait(false);
        }
        // The skip is only an optimization: an unreadable revision or catalogue warms normally,
        // where the executor reports genuine metadata failures.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or NzbWebDAV.Exceptions.CorruptedBlobPayloadException)
        { return false; }
    }

    // Identity lookups hash the item's source metadata, so a status poll reuses them briefly.
    private static readonly TimeSpan IdentityLifetime = TimeSpan.FromSeconds(30);
    private const int MaxCoverageItems = 64;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, NativeCacheIdentity? Identity)> _identities = new();

    /// <summary>
    /// Own-range cache coverage of range jobs (backfill, minimum head/tail, resume range): the bytes of
    /// each job's block-aligned range verified in the catalogue for the item's current revision. Whole-file
    /// jobs, removed media, and items whose revision is unknown are omitted. Bounded to the most recently
    /// updated items so a long history cannot turn a status poll into a library scan.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (long Bytes, long Cached)>> GetRangeCoverageAsync(
        IEnumerable<PrefetchJob> jobs, IReadOnlyDictionary<Guid, DavItem> items, CancellationToken ct)
    {
        using var timing = new NzbWebDAV.Services.Observability.PageLoadTiming("prefetch.coverage");
        var result = new Dictionary<string, (long Bytes, long Cached)>(StringComparer.Ordinal);
        if (native.Store is not { } store) return result;
        var selected = jobs.ToArray();
        var resolved = new Dictionary<Guid, NativeCacheIdentity?>();
        foreach (var job in selected)
        {
            if (!job.IsRangeJob || items.GetValueOrDefault(job.ItemId) is not { FileSize: > 0 } item) continue;
            if (!resolved.TryGetValue(job.ItemId, out var identity))
            {
                if (resolved.Count >= MaxCoverageItems) continue;
                try { identity = await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or Microsoft.Data.Sqlite.SqliteException or NzbWebDAV.Exceptions.CorruptedBlobPayloadException or ObjectDisposedException)
                { identity = null; }
                resolved[job.ItemId] = identity;
            }
            if (identity is null || job.Start >= identity.Length
                || (job.Length != 0 && job.Length > identity.Length - job.Start)) continue;
            var (start, end) = NativePrefetchExecutor.AlignedRange(job.Start, job.Length, identity.Length);
            try
            {
                var missing = await store.GetMissingRangeBytesAsync(identity, start, end, ct).ConfigureAwait(false);
                result[job.Id] = (end - start, end - start - missing);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            { break; }
        }
        // Revalidate once per item after all range sums, including on a partial metadata failure.
        foreach (var (id, identity) in resolved)
        {
            if (identity is null) continue;
            NativeCacheIdentity? current;
            try { current = await native.GetCurrentCacheIdentityAsync(items[id], ct).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException or NzbWebDAV.Exceptions.CorruptedBlobPayloadException or ObjectDisposedException)
            { current = null; }
            if (current != identity)
                foreach (var job in selected.Where(job => job.ItemId == id)) result.Remove(job.Id);
        }
        return result;
    }

    /// <summary>
    /// True while warming has proven this item's release damaged and its revision has not changed since,
    /// so routine producers (policies, backfill, finish-watched) leave it to repair. Manual requests ignore it.
    /// </summary>
    public async Task<bool> IsKnownDamagedAsync(DavItem item, CancellationToken ct)
    {
        if (Jobs is not { } jobs) return false;
        var now = _clock.GetUtcNow();
        try
        {
            // Cheap check first: the revision is only resolved for items that carry a verdict.
            if (!jobs.IsDamaged(item.Id, null, now)) return false;
            var identity = await CachedIdentityAsync(item, ct).ConfigureAwait(false);
            return jobs.IsDamaged(item.Id, identity?.Generation, now);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        { return false; }
    }

    private async Task<NativeCacheIdentity?> CachedIdentityAsync(DavItem item, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_identities.TryGetValue(item.Id, out var cached) && now - cached.At < IdentityLifetime) return cached.Identity;
        NativeCacheIdentity? identity;
        try { identity = await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or NzbWebDAV.Exceptions.CorruptedBlobPayloadException)
        { identity = null; }
        if (_identities.Count >= 512) _identities.Clear();
        _identities[item.Id] = (now, identity);
        return identity;
    }

    public PrefetchSettings Settings()
    {
        var json = config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings);
        var snapshot = Volatile.Read(ref _settings);
        if (snapshot is not null && string.Equals(snapshot.Json, json, StringComparison.Ordinal)) return snapshot.Value;
        var parsed = PrefetchSettings.Parse(json);
        Volatile.Write(ref _settings, new(json, parsed));
        return parsed;
    }

    /// <summary>
    /// Writes debounced backfill requests to the queue: due items, or every pending item when
    /// <paramref name="all"/>. Each range merges with nearby queued work or reopens a recently
    /// completed backfill job of the same cache revision.
    /// </summary>
    public async Task FlushBackfillAsync(bool all, CancellationToken ct)
    {
        var due = _backfill.TakeDue(all);
        if (due.Count == 0 || Jobs is not { } jobs) return;
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<NzbWebDAV.Database.DavDatabaseClient>();
        foreach (var (itemId, ranges, reason) in due)
        {
            try
            {
                var item = await database.GetFileById(itemId.ToString()).ConfigureAwait(false);
                if (item is null) continue;
                var identity = await CachedIdentityAsync(item, ct).ConfigureAwait(false);
                if (jobs.IsDamaged(itemId, identity?.Generation, _clock.GetUtcNow())) continue;
                var since = _clock.GetUtcNow() - BackfillReviveWindow;
                var viewer = Viewers.Find(itemId);
                foreach (var (start, length) in ranges)
                {
                    var queued = jobs.EnqueueBackfill(itemId, BackfillOwner, BackfillPriority, start, length,
                        identity?.Generation, BackfillCoalescer.MergeGap, since);
                    jobs.RecordBackfillContext(queued.Job.Id, reason, viewer);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or ObjectDisposedException or IOException or Microsoft.Data.Sqlite.SqliteException)
            { Log.Debug(exception, "Could not schedule Native Cache backfill for {ItemId}", itemId); }
        }
    }

    private async Task FlushBackfillLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _clock, stoppingToken).ConfigureAwait(false);
                try { await FlushBackfillAsync(all: false, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { Log.Debug(exception, "Native Cache backfill flush failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,
            applicationLifetime?.ApplicationStopping ?? CancellationToken.None);
        stoppingToken = stopping.Token;
        var backfill = FlushBackfillLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Healthy && Coordinator is { } coordinator) await coordinator.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException) { ReportMetadataFailure(); }
                await Task.Delay(TimeSpan.FromSeconds(Healthy ? 1 : 30), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await backfill.ConfigureAwait(false);
    }

    public override void Dispose()
    {
        base.Dispose();
        _coordinator?.Dispose();
        _jobs?.Dispose();
    }
}
