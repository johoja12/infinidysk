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
    IHostApplicationLifetime? applicationLifetime = null) : BackgroundService
{
    /// <summary>Owner of jobs that fill in playback a foreground stream could not cache.</summary>
    public const string BackfillOwner = "backfill";
    // Above manual warming (50): someone just watched these bytes.
    private const int BackfillPriority = 60;
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
                var jobs = _jobs;
                native.BackfillSink = (itemId, start, length) =>
                {
                    try { jobs.Enqueue(itemId, BackfillOwner, BackfillPriority, start, length); }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                        or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
                    { Log.Debug(exception, "Could not schedule Native Cache backfill for {ItemId}", itemId); }
                };
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

    public PrefetchSettings Settings()
    {
        var json = config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings);
        var snapshot = Volatile.Read(ref _settings);
        if (snapshot is not null && string.Equals(snapshot.Json, json, StringComparison.Ordinal)) return snapshot.Value;
        var parsed = PrefetchSettings.Parse(json);
        Volatile.Write(ref _settings, new(json, parsed));
        return parsed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,
            applicationLifetime?.ApplicationStopping ?? CancellationToken.None);
        stoppingToken = stopping.Token;
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
    }

    public override void Dispose()
    {
        base.Dispose();
        _coordinator?.Dispose();
        _jobs?.Dispose();
    }
}
