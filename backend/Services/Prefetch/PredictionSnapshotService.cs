using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Services.Prefetch;

public sealed record PredictionSnapshot(IReadOnlyList<PrefetchPrediction> Predictions, DateTimeOffset? UpdatedAt,
    bool HasSnapshot, bool Refreshing, bool Stale, string? Error, bool PlexUnavailable = false, string? Warning = null);
public sealed record PredictionRefresh(IReadOnlyList<PrefetchPrediction> Predictions, bool Complete, string? Error, bool PlexUnavailable = false);

/// <summary>One bounded read-only refresh shared by all visitors. No request owns its lifetime.</summary>
public sealed class PredictionSnapshotCache(Func<string> revision, Func<CancellationToken, Task<PredictionRefresh>> fetch,
    TimeProvider clock, CancellationToken stoppingToken, PredictionSnapshotStore? store = null)
{
    private readonly object _gate = new();
    private string? _revision;
    private IReadOnlyList<PrefetchPrediction> _predictions = [];
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset _retryAt;
    private Task? _refresh;
    private string? _error;
    private bool _restored;
    private bool _plexUnavailable;

    public PredictionSnapshot Get()
    {
        lock (_gate)
        {
            var current = revision();
            if (_revision != current)
            {
                _revision = current;
                _predictions = [];
                _updatedAt = null;
                _error = null;
                _plexUnavailable = false;
                var saved = store?.Load(current);
                _restored = saved is not null;
                if (saved is not null)
                {
                    _predictions = saved.Predictions;
                    _updatedAt = saved.UpdatedAt;
                }
                _retryAt = DateTimeOffset.MinValue;
            }
            var now = clock.GetUtcNow();
            if ((_refresh is null || _refresh.IsCompleted) && now >= _retryAt && !stoppingToken.IsCancellationRequested)
            {
                _retryAt = now.AddSeconds(30);
                _refresh = Task.Run(() => RefreshAsync(current), CancellationToken.None);
            }
            return new(_predictions, _updatedAt, _updatedAt is not null, _refresh is { IsCompleted: false },
                _restored || _error is not null || _updatedAt is null || now - _updatedAt >= TimeSpan.FromMinutes(1),
                _error, _plexUnavailable, store?.Warning);
        }
    }

    private async Task RefreshAsync(string expected)
    {
        PredictionRefresh result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            result = await fetch(timeout.Token).ConfigureAwait(false);
            if (result.Predictions.Count > 100)
                result = new([], false, "Prediction refresh exceeded its candidate limit.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Do not expose transport exception text, credentials, or partial results.
            result = new([], false, "Prediction refresh could not complete. Retrying shortly.");
        }
        lock (_gate)
        {
            if (_revision != expected || revision() != expected) return;
            _plexUnavailable = !result.Complete && result.PlexUnavailable;
            _error = result.Complete ? null : result.Error ?? "Prediction refresh is incomplete. Retrying shortly.";
            if (result.Complete)
            {
                _predictions = result.Predictions.ToArray();
                _updatedAt = clock.GetUtcNow();
                _restored = false;
                store?.Save(new(1, expected, _updatedAt.Value, _predictions));
            }
            _retryAt = clock.GetUtcNow().AddSeconds(result.Complete ? 60 : 30);
        }
    }
}

public sealed class PredictionSnapshotService(PlexPrefetchService policies, ConfigManager config,
    PlexCatalogueService catalogue, PrefetchRuntime runtime, IHostApplicationLifetime lifetime)
{
    private readonly PredictionSnapshotCache _cache = new(policies.PreviewRevision,
        ct => RefreshAsync(policies, config, catalogue, runtime, ct), TimeProvider.System, lifetime.ApplicationStopping,
        new PredictionSnapshotStore(Path.Combine(DavDatabaseContext.ConfigPath, "prefetch-predictions.json")));

    public PredictionSnapshot Get() => _cache.Get();

    private static async Task<PredictionRefresh> RefreshAsync(PlexPrefetchService policies, ConfigManager config,
        PlexCatalogueService catalogue, PrefetchRuntime runtime, CancellationToken ct)
    {
        var pass = await policies.PreviewSnapshotAsync(ct).ConfigureAwait(false);
        if (!pass.Complete) return new([], false, pass.Error, pass.PlexUnavailable);
        var predictions = pass.Predictions;
        var servers = PlexSettings.ParseServers(config.GetEffectiveConfigValue(ConfigKeys.PlexServers));
        var hashes = predictions.Select(prediction => prediction.Owner?.Split(':'))
            .Where(parts => parts is { Length: >= 4 } && parts[0] == "plex")
            .Select(parts => parts![1]).ToHashSet(StringComparer.Ordinal);
        using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookupTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var snapshots = await Task.WhenAll(servers.Where(server => server.Enabled && hashes.Contains(PlexPrefetchService.Hash(server.Id)))
            .Select(async server =>
            {
                try
                {
                    var users = await catalogue.GetUsersAsync(server, ct: lookupTimeout.Token).ConfigureAwait(false);
                    return users.Data.Select(user => new KeyValuePair<string, string>(
                        PlexPrefetchService.Hash(server.Id) + ":" + PlexPrefetchService.Hash(user.Id), user.Name)).ToArray();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { return Array.Empty<KeyValuePair<string, string>>(); }
            })).ConfigureAwait(false);
        var names = snapshots.SelectMany(snapshot => snapshot).GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        return new(predictions.Select(prediction =>
        {
            var parts = prediction.Owner?.Split(':');
            return prediction with
            {
                Attribution = PlexPrefetchService.DescribeOwner(prediction.Owner ?? "", runtime.Settings(), servers, names),
                Viewer = parts is { Length: >= 4 } && parts[2] is "history-next" or "realtime-next"
                    ? names.GetValueOrDefault(parts[1] + ":" + parts[3]) ?? "Unknown viewer" : null
            };
        }).ToArray(), true, null);
    }
}
