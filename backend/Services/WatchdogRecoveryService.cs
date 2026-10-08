using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

/// <summary>Read-only, bounded Arr history evidence; never infer recovery from release names.</summary>
public sealed class WatchdogRecoveryService : IDisposable
{
    private readonly Func<IEnumerable<ArrClient>> clientsFactory;

    public WatchdogRecoveryService(ConfigManager configManager)
        : this(() => configManager.GetArrConfig().GetArrClients()) { }

    internal WatchdogRecoveryService(Func<IEnumerable<ArrClient>> clientsFactory)
        => this.clientsFactory = clientsFactory;

    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private DateTimeOffset refreshAfter;
    private Task? refreshTask;
    private List<IReadOnlyList<ArrHistoryRecord>> histories = [];

    public Task<Dictionary<Guid, ArrHistoryRecord>> GetRecoveriesAsync(
        IReadOnlyList<WatchdogEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (entries.Any(x => x.Result == WatchdogEntry.Outcome.QueueFailed)
                && DateTimeOffset.UtcNow >= refreshAfter
                && (refreshTask == null || refreshTask.IsCompleted))
            {
                // Arr bulk history can take seconds on a NAS. Never block UI polling on it.
                refreshTask = Task.Run(RefreshAsync, lifetime.Token);
            }
            return Task.FromResult(FindRecoveries(entries, histories));
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var clients = clientsFactory().Take(16);
            var fetched = await Task.WhenAll(clients.Select(async client =>
            {
                try { return (IReadOnlyList<ArrHistoryRecord>)(await client.GetRecentHistoryAsync(timeout.Token).ConfigureAwait(false)).Records; }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
                { return (IReadOnlyList<ArrHistoryRecord>)Array.Empty<ArrHistoryRecord>(); }
            })).ConfigureAwait(false);
            lock (gate) histories = fetched.ToList();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException)
        {
            Serilog.Log.Warning("Watchdog replacement history refresh failed ({ErrorType})", e.GetType().Name);
        }
        finally
        {
            lock (gate) refreshAfter = DateTimeOffset.UtcNow.AddMinutes(1);
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try { refreshTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
    }

    internal static Dictionary<Guid, ArrHistoryRecord> FindRecoveries(
        IReadOnlyList<WatchdogEntry> entries, IEnumerable<IReadOnlyList<ArrHistoryRecord>> histories)
    {
        var result = new Dictionary<Guid, ArrHistoryRecord>();
        foreach (var entry in entries.Where(x => x.Result == WatchdogEntry.Outcome.QueueFailed && x.QueueItemId != null))
        {
            // Keep identities scoped to one Arr instance. Reject ambiguous multi-episode downloads.
            var matches = new List<ArrHistoryRecord>();
            var owners = 0;
            foreach (var history in histories)
            {
                var originals = history.Where(x => Guid.TryParse(x.DownloadId, out var id) && id == entry.QueueItemId).ToList();
                if (originals.Count == 0) continue;
                owners++;
                var identities = originals.Select(x => (x.EpisodeId, x.MovieId)).Distinct().ToList();
                if (identities.Count != 1) continue;
                var identity = identities[0];
                if ((identity.EpisodeId.GetValueOrDefault() > 0) == (identity.MovieId.GetValueOrDefault() > 0)) continue;
                var replacement = history.Where(x => x.EventType == 3
                    && (x.EpisodeId, x.MovieId) == identity
                    && x.Date > entry.AttemptedAt
                    && Guid.TryParse(x.DownloadId, out var id) && id != entry.QueueItemId
                    && !string.IsNullOrWhiteSpace(x.Data?.ImportedPath)
                    && !string.IsNullOrWhiteSpace(x.SourceTitle))
                    .OrderByDescending(x => x.Date).FirstOrDefault();
                if (replacement != null) matches.Add(replacement);
            }
            if (owners == 1 && matches.Count == 1) result[entry.ClickId] = matches[0];
        }
        return result;
    }
}
