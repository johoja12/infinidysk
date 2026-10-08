using NzbWebDAV.Clients.RadarrSonarr;
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

    [Fact]
    public async Task SlowArrHistoryDoesNotBlockPollingAndPublishesRecoveryWhenReady()
    {
        var client = new SlowHistoryClient();
        using var service = new WatchdogRecoveryService(() => [client]);
        var first = service.GetRecoveriesAsync([Failed()], CancellationToken.None);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Empty(await first);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await service.GetRecoveriesAsync([Failed()], CancellationToken.None));
        client.History.SetResult(new ArrHistory { Records = [Original(), Imported()] });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while ((await service.GetRecoveriesAsync([Failed()], CancellationToken.None)).Count == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Completed history did not reach the polling cache");
            await Task.Delay(10);
        }
        Assert.Equal(1, client.Calls);
    }

    private sealed class SlowHistoryClient() : ArrClient("http://unused", "unused")
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ArrHistory> History { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public override Task<ArrHistory> GetRecentHistoryAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            return History.Task.WaitAsync(ct);
        }
    }

}
