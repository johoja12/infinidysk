using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

/// <summary>Read-only, bounded Arr history evidence; never infer recovery from release names.</summary>
public sealed class WatchdogRecoveryService(ConfigManager configManager) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset refreshAfter;
    private List<IReadOnlyList<ArrHistoryRecord>> histories = [];

    public void Dispose() => gate.Dispose();

    public async Task<Dictionary<Guid, ArrHistoryRecord>> GetRecoveriesAsync(
        IReadOnlyList<WatchdogEntry> entries, CancellationToken ct)
    {
        if (!entries.Any(x => x.Result == WatchdogEntry.Outcome.QueueFailed)) return [];
        await gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow >= refreshAfter)
            {
                // Bound both upstream work and latency; polling the UI must not poll every Arr.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var clients = configManager.GetArrConfig().GetArrClients().Take(16);
                var fetched = await Task.WhenAll(clients.Select(async client =>
                {
                    try { return (IReadOnlyList<ArrHistoryRecord>)(await client.GetRecentHistoryAsync(timeout.Token)).Records; }
                    catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
                    { return (IReadOnlyList<ArrHistoryRecord>)Array.Empty<ArrHistoryRecord>(); }
                }));
                ct.ThrowIfCancellationRequested();
                histories = fetched.ToList();
                refreshAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            return FindRecoveries(entries, histories);
        }
        finally { gate.Release(); }
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
