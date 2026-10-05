using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Utils;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NzbWebDAV.Services.Prefetch;

public sealed record PrefetchPrediction(Guid ItemId, string DisplayName, string Source, string Reason, long Start, long Length, long FileSize)
{
    public bool Eligible { get; init; } = true;
    public string? PlexRatingKey { get; init; }
    public string? ShowTitle { get; init; }
    public string? EpisodeTitle { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? Viewer { get; init; }
    public string? WatchedStatus { get; init; }
    public string? WatchedWarning { get; init; }
    public string? ServerName { get; init; }
    public PrefetchJobSource? Attribution { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Owner { get; init; }
}

public sealed class PlexPrefetchService(ConfigManager config, PlexApiClient api, PrefetchRuntime runtime,
    IServiceScopeFactory scopes, ActiveReadRegistry reads, PlexPlaybackRegistry? playback = null) : BackgroundService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Managed-only semaphore: in-flight HTTP previews can release after hosted-service disposal. AvailableWaitHandle is never used.")]
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly PlexPlaybackRegistry _playback = playback ?? new(TimeProvider.System);
    private int _requested;
    private int _remainingCandidates;
    private int _serverCursor;
    private string? _policyRevision;
    private string? _passRevision;
    private PlexViewerResolver? _viewerResolver;
    private List<PrefetchPrediction>? _preview;
    public DateTimeOffset? LastSuccess { get; private set; }
    public string? LastError { get; private set; }
    private bool _previewComplete;
    public void RequestSync() => Interlocked.Exchange(ref _requested, 1);

    public Task SyncAsync(bool force, CancellationToken ct) => RunAsync(force, null, ct);
    public async Task<IReadOnlyList<PrefetchPrediction>> PreviewAsync(CancellationToken ct)
    {
        var preview = new List<PrefetchPrediction>();
        await RunAsync(true, preview, ct).ConfigureAwait(false);
        return preview;
    }

    public async Task<PredictionRefresh> PreviewSnapshotAsync(CancellationToken ct)
    {
        var preview = new List<PrefetchPrediction>();
        var complete = false;
        string? error = null;
        await RunAsync(true, preview, ct, (success, failure) => { complete = success; error = failure; }).ConfigureAwait(false);
        return new(preview, complete, error);
    }

    private async Task RunAsync(bool force, List<PrefetchPrediction>? preview, CancellationToken ct, Action<bool, string?>? completed = null)
    {
        if (!await _sync.WaitAsync(0, ct).ConfigureAwait(false))
        {
            if (preview is not null) throw new ArgumentException("A policy refresh is already running. Retry the preview shortly.");
            return;
        }
        _preview = preview;
        if (preview is not null) _previewComplete = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(preview is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(20));
        try
        {
            if (runtime.RuntimeError is not null) { LastError = runtime.RuntimeError; return; }
            var settings = runtime.Settings();
            var servers = PlexSettings.ParseServers(config.GetEffectiveConfigValue(ConfigKeys.PlexServers));
            if (preview is null) _playback.RetainServers(settings.Enabled && settings.RealtimeEnabled
                ? servers.Where(server => server.Enabled).Select(server => server.Id).ToArray() : []);
            var revision = ConfigurationRevision(settings, servers);
            _passRevision = revision;
            _viewerResolver = new(api, PlexSettings.ParseAccounts(config.GetEffectiveConfigValue(ConfigKeys.PlexAccounts)));
            if (preview is null && _policyRevision != revision)
            {
                _last.Clear();
                if (_policyRevision is not null)
                    runtime.Coordinator?.PruneOwners(owner => IsSystemOwner(owner) || IsPlaybackOwnerEnabled(owner, settings));
                _policyRevision = revision;
            }
            await runtime.WaitForInitializationAsync(deadline.Token).ConfigureAwait(false);
            if (!settings.Enabled) { LastError = null; if (preview is not null) _previewComplete = true; return; }
            var jobs = runtime.Jobs;
            if (jobs is null) { LastError = "Activate Native Cache and restart to load prediction results."; return; }
            if (preview is null) runtime.Coordinator!.PruneOwners(owner => IsOwnerEnabled(owner, settings, servers));
            LastError = null;
            _remainingCandidates = preview is null ? 2048 : 100;
            var enabled = servers.Where(server => server.Enabled).ToArray();
            for (var index = 0; index < enabled.Length; index++)
            {
                var server = enabled[(_serverCursor + index) % enabled.Length];
                try { await SyncServerAsync(server, settings, force, deadline.Token).ConfigureAwait(false); }
                catch (PlexRequestException) { LastError = "A Plex server could not be refreshed. Other servers and cached content remain available."; }
                finally { if (index == enabled.Length - 1) _serverCursor = (_serverCursor + 1) % Math.Max(1, enabled.Length); }
            }
            if (settings.ReadActivityEnabled)
            {
                // Raw reads are low-confidence hints only. They never enter the verified Plex registry.
                using var scope = scopes.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                foreach (var read in reads.Snapshot().Where(read => read.QualifiesForWarming(DateTimeOffset.UtcNow)).Take(32))
                {
                    var item = await ResolveDavAsync(database, read.Path, deadline.Token).ConfigureAwait(false);
                    if (item is not null) await QueueImportedAsync(item, "read", 5, 0, 0, settings, deadline.Token).ConfigureAwait(false);
                }
            }
            if (settings.FinishWatchedEnabled && preview is null)
                await QueueFinishWatchedAsync(settings, deadline.Token).ConfigureAwait(false);
            if (preview is null) LastSuccess = DateTimeOffset.UtcNow;
            else _previewComplete = LastError is null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { LastError = "Policy refresh reached its bounded time window; remaining sources will be retried."; _serverCursor++; }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { runtime.ReportMetadataFailure(); LastError = runtime.RuntimeError; }
        finally { completed?.Invoke(_previewComplete, LastError); _preview = null; _sync.Release(); }
    }

    private async Task SyncServerAsync(PlexServer server, PrefetchSettings settings, bool force, CancellationToken ct)
    {
        if (settings.RealtimeEnabled && Due("sessions:" + server.Id, TimeSpan.FromSeconds(settings.RealtimeCheckIntervalSeconds), force))
        {
            try
            {
            var sessions = await api.GetSessionsAsync(server, ct).ConfigureAwait(false);
            if (_preview is null) _playback.Record(server.Id, sessions, TimeSpan.FromSeconds(settings.VerifiedSessionExpirySeconds));
            foreach (var session in sessions.Where(session => session.State is "playing" or "paused"
                && UserSelected(settings, server.Id, session.UserId)).Take(32))
            {
                var owner = Owner(server.Id, "realtime", session.UserId);
                var viewer = new PlaybackViewer(session.UserName, session.PlayerName, session.Item.Duration);
                if (session.Item.ViewOffset > 0) await QueueMediaAsync(server, session.Item, owner, 80, settings, null, ct,
                    resolved: imported => runtime.Viewers.Note(imported.Id, viewer)).ConfigureAwait(false);
                if (settings.PredictionsEnabled && session.Item.Type == "episode")
                    await QueueNextAsync(server, session.Item, Owner(server.Id, "realtime-next", session.UserId), 90, settings, null, ct).ConfigureAwait(false);
            }
            }
            catch (PlexRequestException) { LastError = "Plex playback could not be refreshed; other source policies remain available."; }
        }
        if (settings.HistoryEnabled && Due("history:" + server.Id, TimeSpan.FromMinutes(settings.SyncIntervalMinutes), force))
        {
            try
            {
            var history = await api.GetHistoryAsync(server, DateTimeOffset.UtcNow.AddDays(-settings.LookbackDays), 1000, ct).ConfigureAwait(false);
            foreach (var item in PrefetchPolicy.HistoryCandidates(history, settings, server.Id, DateTimeOffset.UtcNow))
            {
                var owner = Owner(server.Id, "history", item.UserId ?? "");
                if (item.Type == "movie" || item.ViewOffset > 0) await QueueMediaAsync(server, item, owner, 40, settings, null, ct).ConfigureAwait(false);
                if (settings.PredictionsEnabled && item.Type == "episode")
                    await QueueNextAsync(server, item, Owner(server.Id, "history-next", item.UserId ?? ""), 50, settings, null, ct).ConfigureAwait(false);
            }
            }
            catch (PlexRequestException) { LastError = "Plex history could not be refreshed; other source policies remain available."; }
        }
        foreach (var source in settings.Sources.Where(source => source.Enabled && source.ServerId == server.Id
            && !settings.IsLibraryDisabled(source)))
        {
            if (_remainingCandidates <= 0) break;
            ct.ThrowIfCancellationRequested();
            if (source.Type == "movie" ? !settings.MovieEnabled : !settings.TvEnabled) continue;
            var owner = Owner(server.Id, "source", source.Kind + ":" + source.Key);
            var interval = source.Type == "movie" ? settings.MovieSyncIntervalMinutes : settings.TvSyncIntervalMinutes;
            if (!Due(owner, TimeSpan.FromMinutes(interval), force)) continue;
            try
            {
                var items = await api.GetPreviewAsync(server, source.Key, source.Limit, ct).ConfigureAwait(false);
                foreach (var item in items.Take(source.Limit))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!PrefetchPolicy.IsEligible(item, settings, source)) { RejectPreview(item, owner, "Excluded show or disabled media policy."); continue; }
                    if (item.Type == "show")
                    {
                        var users = settings.Users.Where(user => user.StartsWith(server.Id + ":", StringComparison.Ordinal))
                            .Select(user => user[(server.Id.Length + 1)..]).Take(16).ToArray();
                        if (users.Length == 0) users = [server.AccountId ?? ""];
                        foreach (var user in users)
                            await QueueNextAsync(server, item with { UserId = user }, owner, 20, settings, source, ct).ConfigureAwait(false);
                    }
                    else await QueueMediaAsync(server, item, owner, 20, settings, source, ct).ConfigureAwait(false);
                }
            }
            catch (PlexRequestException) { LastError = "A selected Plex source could not be refreshed; its previous cache is retained."; }
        }
    }

    private async Task QueueNextAsync(PlexServer server, PlexMediaItem current, string owner, int priority,
        PrefetchSettings settings, PrefetchSource? source, CancellationToken ct)
    {
        if (_remainingCandidates <= 0 || !settings.TvEnabled || !PrefetchPolicy.IsEligible(current, settings, source)) return;
        var show = current.ShowRatingKey ?? (current.Type == "show" ? current.RatingKey : null);
        if (show is null) return;
        var resolution = await _viewerResolver!.ResolveAsync(server, current.UserId, ct).ConfigureAwait(false);
        var episodes = await api.GetNextEpisodesAsync(resolution.Server ?? server, current, 20, ct).ConfigureAwait(false);
        var remaining = Math.Min(settings.MaxQueueAhead, settings.TvEpisodesPerShow);
        foreach (var item in PrefetchPolicy.NextEpisodes(current, episodes, 20))
        {
            var before = _preview?.Count ?? 0;
            var queued = await QueueMediaAsync(server, item, owner, priority, settings, source, ct, prediction: true).ConfigureAwait(false);
            if (_preview is { } preview)
                for (var index = before; index < preview.Count; index++)
                    preview[index] = preview[index] with { WatchedStatus = resolution.Status, WatchedWarning = resolution.Message, ServerName = server.Name };
            if (queued && --remaining == 0) break;
        }
    }

    public string PreviewRevision() => ConfigurationRevision(runtime.Settings(),
        PlexSettings.ParseServers(config.GetEffectiveConfigValue(ConfigKeys.PlexServers)));

    private string ConfigurationRevision(PrefetchSettings settings, IReadOnlyList<PlexServer> servers) =>
        Hash(JsonSerializer.Serialize(settings) + JsonSerializer.Serialize(servers) + config.GetEffectiveConfigValue(ConfigKeys.PlexAccounts));

    private bool RejectPreview(PlexMediaItem item, string owner, string reason, DavItem? imported = null)
    {
        if (_preview is { Count: < 100 } preview)
            preview.Add(new(imported?.Id ?? Guid.Empty, imported?.Name ?? item.Title, SourceLabel(owner), reason, 0, 0, imported?.FileSize ?? 0)
                { Eligible = false, PlexRatingKey = item.RatingKey, Owner = owner,
                    ShowTitle = item.ShowTitle, EpisodeTitle = item.Title, Season = item.Season, Episode = item.Episode });
        return false;
    }

    private async Task<bool> QueueMediaAsync(PlexServer server, PlexMediaItem item, string owner, int priority,
        PrefetchSettings settings, PrefetchSource? source, CancellationToken ct, bool prediction = false,
        Action<DavItem>? resolved = null)
    {
        if (--_remainingCandidates < 0) return false;
        if (!PrefetchPolicy.IsEligible(item, settings, source)) return RejectPreview(item, owner, "Excluded show or disabled media policy.");
        if (item.File is null)
        {
            var details = await api.GetMetadataAsync(server, item.RatingKey, ct).ConfigureAwait(false);
            item = details with { ViewOffset = item.ViewOffset, Duration = item.Duration > 0 ? item.Duration : details.Duration,
                UserId = item.UserId, ViewedAt = item.ViewedAt, ShowRatingKey = details.ShowRatingKey ?? item.ShowRatingKey,
                ShowTitle = details.ShowTitle ?? item.ShowTitle, Season = details.Season ?? item.Season, Episode = details.Episode ?? item.Episode, WatchStateUserId = item.WatchStateUserId };
        }
        if (item.File is null || PrefetchPathResolver.Map(item.File, server.PathMappings) is not { } mapped)
            return RejectPreview(item, owner, "No exact configured path mapping for this Plex media.");
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        DavItem? imported = null;
        if (mapped.DavPath is { } dav) imported = await ResolveDavAsync(database, dav, ct).ConfigureAwait(false);
        else if (mapped.LocalPath is { } local)
        {
            if (!settings.WarmLocalFiles) return RejectPreview(item, owner, "Mapped local-library warming is disabled.");
            try
            {
                var link = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(local));
                var target = link switch
                {
                    SymlinkAndStrmUtil.SymlinkInfo symlink => OrganizedLinksUtil.GetDavItemLink(symlink, config.GetRcloneMountDir()),
                    SymlinkAndStrmUtil.StrmInfo strm => OrganizedLinksUtil.GetDavItemLink(strm),
                    _ => null
                };
                if (target is { } match) imported = await database.GetFileById(match.DavItemId.ToString()).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return RejectPreview(item, owner, "Local mapping is unavailable or does not resolve to imported media."); }
        }
        var scopedOwner = owner + ":" + item.Type + ":" + Hash(item.ShowRatingKey ?? item.RatingKey)
            + (mapped.LocalPath is null ? "" : ":local")
            + (prediction ? item.WatchStateUserId is null ? ":watch-unknown" : ":unwatched" : "");
        if (imported is null) return RejectPreview(item, owner, "Mapping does not resolve to an imported media file.");
        if (_preview is null) resolved?.Invoke(imported);
        if (imported.FileSize is not > 0 || imported.FileSize > runtime.Settings().MaxBytesPerItem
            || imported.FileSize < NativeCacheSettings.MinimumFileBytes(config) || imported.FileBlobId is null)
            return RejectPreview(item, owner, "Imported media is unavailable or exceeds the per-file warming cap.", imported);
        var previewStart = _preview?.Count ?? 0;
        var accepted = await QueueImportedAsync(imported, scopedOwner, priority, item.ViewOffset, item.Duration, settings, ct).ConfigureAwait(false);
        if (_preview is { } preview)
            for (var index = previewStart; index < preview.Count; index++)
                preview[index] = preview[index] with { Owner = scopedOwner, PlexRatingKey = item.RatingKey,
                    ShowTitle = item.ShowTitle, EpisodeTitle = item.Title, Season = item.Season, Episode = item.Episode };
        return accepted;
    }

    /// <summary>
    /// Opt-in completion of partially watched files: a sustained foreground read session that served at
    /// least <see cref="PrefetchSettings.FinishWatchedPercent"/> of a file queues the whole file.
    /// </summary>
    private async Task QueueFinishWatchedAsync(PrefetchSettings settings, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var watched = reads.Snapshot().Where(read => QualifiesAsWatched(read, settings, now)).Take(32).ToArray();
        if (watched.Length == 0) return;
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        foreach (var read in watched)
        {
            if (!Due("finish-watched:" + read.Path, TimeSpan.FromMinutes(settings.CooldownMinutes), force: false)) continue;
            var item = await ResolveDavAsync(database, read.Path, ct).ConfigureAwait(false);
            if (item is not null)
                await QueueImportedAsync(item, FinishWatchedOwner, FinishWatchedPriority, 0, 0, settings, ct, wholeFile: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True for a playback-length session: it served the configured share of the file over at least
    /// <see cref="FinishWatchedMinimumSession"/>, so a fast library scan of the same bytes does not count.
    /// </summary>
    internal static bool QualifiesAsWatched(ActiveReadRegistry.Entry read, PrefetchSettings settings, DateTimeOffset now)
    {
        if (read.FileSize is not > 0 || now - read.StartedAt < FinishWatchedMinimumSession) return false;
        var served = Interlocked.Read(ref read.BytesRead);
        return served >= FinishWatchedMinimumBytes && served >= read.FileSize.Value / 100 * settings.FinishWatchedPercent;
    }

    private async Task<bool> QueueImportedAsync(DavItem item, string owner, int priority, long viewOffset, long duration,
        PrefetchSettings settings, CancellationToken ct, bool wholeFile = false)
    {
        var current = runtime.Settings();
        var servers = PlexSettings.ParseServers(config.GetEffectiveConfigValue(ConfigKeys.PlexServers));
        if (_passRevision != ConfigurationRevision(current, servers))
        {
            LastError = "Plex policy or mappings changed during refresh. Stale candidates were skipped; another refresh is requested.";
            RequestSync();
            return false;
        }
        if (item.FileSize is not > 0 || item.FileSize > current.MaxBytesPerItem
            || item.FileSize < NativeCacheSettings.MinimumFileBytes(config) || item.FileBlobId is null
            || !IsOwnerEnabled(owner, current, servers)) return false;
        if (_preview is { } preview)
        {
            foreach (var range in PrefetchPolicy.Ranges(item.FileSize.Value, viewOffset, duration, current, minimum: false))
                if (preview.Count < 100) preview.Add(new(item.Id, item.Name, SourceLabel(owner),
                    RangeReason(owner, range.Length), range.Start, range.Length, item.FileSize.Value));
            if (current.MinimumWarmEnabled && !current.FullFileWarming)
                foreach (var range in PrefetchPolicy.Ranges(item.FileSize.Value, 0, 0, current, minimum: true))
                    if (preview.Count < 100) preview.Add(new(item.Id, item.Name, SourceLabel(owner), "Minimum head/tail", range.Start, range.Length, item.FileSize.Value));
            return true;
        }
        var cooldownKey = "queued:" + item.Id.ToString("N") + ":" + owner;
        if (_last.TryGetValue(cooldownKey, out var previous) && DateTimeOffset.UtcNow - previous < TimeSpan.FromMinutes(current.CooldownMinutes)) return true;
        // A release that warming proved damaged waits for repair instead of failing again every refresh.
        if (await runtime.IsKnownDamagedAsync(item, ct).ConfigureAwait(false)) return false;
        IReadOnlyList<(long Start, long Length)> ranges = wholeFile ? [(0, 0)]
            : PrefetchPolicy.Ranges(item.FileSize.Value, viewOffset, duration, current, minimum: false);
        IReadOnlyList<(long Start, long Length)> minimumRanges = !wholeFile && current.MinimumWarmEnabled && !current.FullFileWarming
            ? PrefetchPolicy.Ranges(item.FileSize.Value, 0, 0, current, minimum: true) : [];
        // Hub and history refreshes rediscover the same media every interval. When a completed warm
        // already verified this revision within the intent window, a new job would only re-read
        // the whole cached file, so treat the intent as satisfied.
        if (await runtime.IsRecentlyWarmAsync(item, [.. ranges, .. minimumRanges], ct).ConfigureAwait(false))
        {
            Due(cooldownKey, TimeSpan.Zero, force: true);
            return true;
        }
        var accepted = false;
        foreach (var range in ranges)
        {
            try { runtime.Jobs!.Enqueue(item.Id, owner, priority, range.Start, range.Length); accepted = true; }
            catch (ArgumentException) { return accepted; }
        }
        foreach (var range in minimumRanges)
            try { runtime.Jobs!.Enqueue(item.Id, owner + ":minimum", priority - 5, range.Start, range.Length); }
            catch (ArgumentException) { break; }
        if (accepted) Due(cooldownKey, TimeSpan.Zero, force: true);
        return accepted;
    }

    private static Task<DavItem?> ResolveDavAsync(DavDatabaseClient database, string path, CancellationToken ct)
    {
        var normalized = path.StartsWith("/view/", StringComparison.Ordinal) ? path[5..] : path;
        if (normalized.StartsWith("/.ids/", StringComparison.Ordinal) && Guid.TryParse(Path.GetFileNameWithoutExtension(normalized), out var id))
            return database.GetFileById(id.ToString());
        return database.GetItemByPathAsync(normalized, ct);
    }

    private bool Due(string key, TimeSpan interval, bool force)
    {
        if (_preview is not null) return true;
        var now = DateTimeOffset.UtcNow;
        if (!force && _last.TryGetValue(key, out var previous) && now - previous < interval) return false;
        if (_last.Count >= 2048 && !_last.ContainsKey(key)) _last.Remove(_last.MinBy(pair => pair.Value).Key);
        _last[key] = now;
        return true;
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    public static string SourceLabel(string owner) => owner.Split(':') switch
    {
        ["manual"] => "Manual",
        [PrefetchRuntime.BackfillOwner] => "Playback not yet cached",
        [FinishWatchedOwner] => "Finish partially watched",
        ["read", ..] => "Read activity (unverified)",
        ["plex", _, "source", ..] => "Selected Plex hub/collection",
        ["plex", _, "realtime-next", ..] => "Plex playback prediction",
        ["plex", _, "history-next", ..] => "Plex history prediction",
        ["plex", _, "realtime", ..] => "Verified Plex playback",
        ["plex", _, "history", ..] => "Plex watch history",
        _ => "Background warming"
    };
    /// <summary>Source categories, most specific first; the frontend colours bubbles by these.</summary>
    private static readonly string[] SourceCategories =
    [
        "plex-realtime", "plex-realtime-next", "plex-history-next", "plex-history", "plex-source",
        "finish-watched", "backfill", "manual", "read", "other",
    ];

    /// <summary>
    /// Resolves a job owner to a concrete label and category. Plex hub and collection owners only
    /// store a hash of the source, so the title is found by matching the same hashes against the
    /// configured sources; a removed source falls back to the generic label.
    /// </summary>
    public static PrefetchJobSource DescribeOwner(string owner, PrefetchSettings settings, IReadOnlyList<PlexServer> servers,
        IReadOnlyDictionary<string, string>? userNames = null)
    {
        var parts = owner.Split(':');
        var minimum = parts.Length > 1 && parts[^1] == "minimum";
        string WithRange(string label) => minimum ? label + " · head/tail" : label;
        string WithUser(string label)
        {
            var name = parts.Length >= 4 ? userNames?.GetValueOrDefault(parts[1] + ":" + parts[3]) : null;
            return WithRange(label + " · " + (string.IsNullOrWhiteSpace(name) ? "Unknown user" : name));
        }
        return parts switch
        {
            ["manual"] => new("Manual", "manual"),
            [PrefetchRuntime.BackfillOwner] => new("Playback not yet cached", "backfill"),
            [FinishWatchedOwner] => new("Finish partially watched", "finish-watched"),
            ["read", ..] => new(WithRange("Read activity"), "read"),
            ["plex", var server, "source", var key, ..] => new(WithRange(SourceTitle(server, key, settings, servers)
                ?? "Selected Plex hub/collection"), "plex-source"),
            ["plex", _, "realtime-next", ..] => new(WithUser("Next episode · realtime"), "plex-realtime-next"),
            ["plex", _, "history-next", ..] => new(WithUser("Next episode · history"), "plex-history-next"),
            ["plex", _, "realtime", ..] => new(WithRange("Playing now"), "plex-realtime"),
            ["plex", _, "history", ..] => new(WithRange("Watch history"), "plex-history"),
            _ => new("Background warming", "other"),
        };
    }

    private static string? SourceTitle(string serverHash, string sourceHash, PrefetchSettings settings, IReadOnlyList<PlexServer> servers)
    {
        var server = servers.FirstOrDefault(candidate => Hash(candidate.Id) == serverHash);
        if (server is null) return null;
        var source = settings.Sources.FirstOrDefault(candidate => candidate.ServerId == server.Id
            && Hash(candidate.Kind + ":" + candidate.Key) == sourceHash);
        return string.IsNullOrWhiteSpace(source?.Title) ? null : source.Title;
    }

    /// <summary>Distinct sources of one job, most specific category first, capped at <paramref name="limit"/>.</summary>
    public static (IReadOnlyList<PrefetchJobSource> Sources, int Count) DescribeOwners(IEnumerable<string> owners,
        PrefetchSettings settings, IReadOnlyList<PlexServer> servers, int limit = 8,
        IReadOnlyDictionary<string, string>? userNames = null)
    {
        var distinct = owners.Select(owner => DescribeOwner(owner, settings, servers, userNames)).Distinct()
            .OrderBy(source => Array.IndexOf(SourceCategories, source.Category)).ThenBy(source => source.Label, StringComparer.Ordinal)
            .ToArray();
        return (distinct.Take(limit).ToArray(), distinct.Length);
    }

    public static string RangeReason(string owner, long length) =>
        (owner == PrefetchRuntime.BackfillOwner ? "Fills in what playback streamed without caching"
            : owner == FinishWatchedOwner ? "Finishes caching a file you started watching"
            : owner.EndsWith(":minimum", StringComparison.Ordinal) ? "Minimum head/tail" : length == 0 ? "Whole-file warming" : "Resume/start range")
        + (owner.Contains(":watch-unknown", StringComparison.Ordinal) ? "; watched status unknown"
            : owner.Contains(":unwatched", StringComparison.Ordinal) ? "; next unwatched episode" : "");
    internal static string Owner(string server, string kind, string key) => $"plex:{Hash(server)}:{kind}:{Hash(key)}";
    private static bool UserSelected(PrefetchSettings settings, string server, string user) => settings.Users.Length == 0
        || settings.Users.Contains(server + ":" + user, StringComparer.Ordinal);
    /// <summary>Owners that are not Plex policy sources and survive every policy change.</summary>
    internal static bool IsSystemOwner(string owner) => owner is "manual" or PrefetchRuntime.BackfillOwner;

    /// <summary>Owner of opt-in whole-file jobs that finish caching a partially watched file.</summary>
    public const string FinishWatchedOwner = "finish-watched";
    // Below backfill (60) and manual (50), above Plex sources (20): someone started watching this file.
    private const int FinishWatchedPriority = 30;
    internal static readonly TimeSpan FinishWatchedMinimumSession = TimeSpan.FromMinutes(2);
    internal const long FinishWatchedMinimumBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Playback-driven owners that are not Plex sources: they keep their work across policy changes
    /// while their setting stays on, and lose it when switched off.
    /// </summary>
    internal static bool IsPlaybackOwnerEnabled(string owner, PrefetchSettings settings) =>
        owner == FinishWatchedOwner && settings.Enabled && settings.FinishWatchedEnabled;

    internal static bool IsOwnerEnabled(string owner, PrefetchSettings settings, IReadOnlyList<PlexServer> servers)
    {
        if (IsSystemOwner(owner)) return true;
        if (owner == FinishWatchedOwner) return IsPlaybackOwnerEnabled(owner, settings);
        if (!settings.Enabled) return false;
        if (owner == "read") return settings.ReadActivityEnabled;
        if (owner == "read:minimum") return settings.ReadActivityEnabled && settings.MinimumWarmEnabled;
        var parts = owner.Split(':');
        if (parts.Length is < 6 or > 9 || parts[0] != "plex") return false;
        foreach (var qualifier in parts.Skip(6))
            if (!(qualifier switch { "minimum" => settings.MinimumWarmEnabled, "local" => settings.WarmLocalFiles,
                "unwatched" or "watch-unknown" => true, _ => false })) return false;
        if (parts[4] == "movie" ? !settings.MovieEnabled : !settings.TvEnabled) return false;
        var server = servers.FirstOrDefault(server => server.Enabled && Hash(server.Id) == parts[1]);
        if (server is null) return false;
        return parts[2] switch
        {
            "source" => settings.Sources.Any(source => source.Enabled && source.ServerId == server.Id && Hash(source.Kind + ":" + source.Key) == parts[3]
                && !source.ExcludedShows.Any(show => Hash(show) == parts[5])),
            "realtime" or "realtime-next" => settings.RealtimeEnabled && (parts[2] == "realtime" || settings.PredictionsEnabled)
                && (settings.Users.Length == 0 || settings.Users.Any(user => user.StartsWith(server.Id + ":", StringComparison.Ordinal) && Hash(user[(server.Id.Length + 1)..]) == parts[3])),
            "history" or "history-next" => settings.HistoryEnabled && (parts[2] == "history" || settings.PredictionsEnabled)
                && (settings.Users.Length == 0 || settings.Users.Any(user => user.StartsWith(server.Id + ":", StringComparison.Ordinal) && Hash(user[(server.Id.Length + 1)..]) == parts[3])),
            _ => false
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncAsync(Interlocked.Exchange(ref _requested, 0) != 0, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { LastError = "Policy refresh failed. Check Plex configuration and imported path mappings."; }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
