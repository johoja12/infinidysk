using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Clients;

public class WatchdogRecoveryTests
{
    private readonly Guid failedId = Guid.NewGuid();
    private readonly DateTimeOffset at = DateTimeOffset.UtcNow;
    private WatchdogEntry Failed() => new() { ClickId = failedId, QueueItemId = failedId,
        Result = WatchdogEntry.Outcome.QueueFailed, AttemptedAt = at };
    private ArrHistoryRecord Original(int episode = 10) => new() { EpisodeId = episode,
        DownloadId = failedId.ToString(), Date = at, EventType = 1 };
    private ArrHistoryRecord Imported(int episode = 10) => new() { EpisodeId = episode,
        DownloadId = Guid.NewGuid().ToString(), Date = at.AddMinutes(2), EventType = 3,
        SourceTitle = "Replacement", Data = new() { ImportedPath = "/library/episode.mkv" } };

    [Fact]
    public void MatchesExactDownloadAndEpisodeInSameInstance()
    {
        var imported = Imported();
        var result = WatchdogRecoveryService.FindRecoveries([Failed()], [[Original(), imported]]);
        Assert.Same(imported, result[failedId]);
    }

    [Fact]
    public void NeverMatchesDifferentEpisodeOrCrossInstance()
    {
        Assert.Empty(WatchdogRecoveryService.FindRecoveries([Failed()], [[Original(), Imported(11)]]));
        Assert.Empty(WatchdogRecoveryService.FindRecoveries([Failed()], [[Original()], [Imported()]]));
    }

    [Fact]
    public void RejectsAmbiguousPackOrMultipleOwningInstances()
    {
        Assert.Empty(WatchdogRecoveryService.FindRecoveries([Failed()], [[Original(), Original(11), Imported()]]));
        Assert.Empty(WatchdogRecoveryService.FindRecoveries([Failed()], [[Original(), Imported()], [Original()]]));
    }

    [Fact]
    public void GrabOrOlderImportDoesNotProveRecovery()
    {
        var imported = Imported();
        imported.Date = at.AddMinutes(-1);
        Assert.Empty(WatchdogRecoveryService.FindRecoveries([Failed()], [[Original(), imported]]));
        imported.Date = at.AddMinutes(1);
        imported.EventType = 1;
        Assert.Empty(WatchdogRecoveryService.FindRecoveries([Failed()], [[Original(), imported]]));
    }
}
