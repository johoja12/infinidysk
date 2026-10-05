using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchLiveStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-live-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RateWindow_ReportsRecentBytesPerSecondOverTheElapsedWindow()
    {
        var rates = new WarmingRateWindow();
        rates.Start("job", T0);
        Assert.Null(rates.Get("job", T0)!.RecentBytesPerSecond); // Nothing measurable yet.

        rates.Record("job", 40_000_000, T0.AddSeconds(2));
        rates.Record("job", 40_000_000, T0.AddSeconds(4));
        Assert.Equal(20_000_000, rates.Get("job", T0.AddSeconds(4))!.RecentBytesPerSecond); // 80 MB over 4 s.

        // Older samples leave the window, so a slowdown shows up quickly.
        rates.Record("job", 10_000_000, T0.AddSeconds(30));
        var later = rates.Get("job", T0.AddSeconds(30))!;
        Assert.Equal(10_000_000 / WarmingRateWindow.Window.TotalSeconds, later.RecentBytesPerSecond);
        Assert.False(later.Stalled);
        Assert.Equal(T0.AddSeconds(30), later.LastProgressAt);
    }

    [Fact]
    public void RateWindow_FlagsStalledJobs_AndCountsVerificationAsProgress()
    {
        var rates = new WarmingRateWindow();
        rates.Start("job", T0);
        Assert.True(rates.Get("job", T0 + WarmingRateWindow.StallAfter)!.Stalled);
        Assert.Equal(0, rates.Get("job", T0 + WarmingRateWindow.StallAfter)!.RecentBytesPerSecond);

        // Re-verifying cached blocks fetches nothing but is still progress.
        rates.Record("job", 0, T0 + WarmingRateWindow.StallAfter);
        Assert.False(rates.Get("job", T0 + WarmingRateWindow.StallAfter + TimeSpan.FromSeconds(5))!.Stalled);

        rates.Stop("job");
        Assert.Null(rates.Get("job", T0));
    }

    [Fact]
    public void RunningJobs_ExposeLiveRateAndCurrentRunTime_FinishedJobsDoNot()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 50);
        jobs.ClaimNext();
        Thread.Sleep(20);
        jobs.Progress(job.Id, "g1", 1000, 1000);
        var running = jobs.List().Single();
        Assert.NotNull(running.LastProgressAt);
        Assert.False(running.Stalled);
        Assert.True(running.ActiveMs > 0); // The current run counts toward the live average.

        jobs.Finish(job.Id, true, null);
        var finished = jobs.List().Single();
        Assert.Null(finished.RecentBytesPerSecond);
        Assert.Null(finished.LastProgressAt);
        Assert.Null(finished.Stalled);
    }

    [Fact]
    public void OwnersOf_ReturnsEveryOwnerOfTheRequestedJobs()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = jobs.Enqueue(item, "plex:a:source:b:movie:c", 20);
        jobs.Enqueue(item, "manual", 50);
        var other = jobs.Enqueue(Guid.NewGuid(), PrefetchRuntime.BackfillOwner, 60, 0, 4096);
        var owners = jobs.OwnersOf([job.Id]);
        Assert.Equal(new[] { "manual", "plex:a:source:b:movie:c" }, owners[job.Id]);
        Assert.False(owners.ContainsKey(other.Id));
    }

    private static readonly PlexServer Server = new() { Id = "machine", Name = "Plex", Url = "http://plex.test:32400", Token = "t" };

    private static PrefetchSettings WithSource(string title) => new()
    {
        Sources = [new PrefetchSource { ServerId = "machine", Kind = "hub", Key = "/hubs/popular-tv", Title = title, Type = "show" }],
    };

    private static string HubOwner(string suffix = "") =>
        PlexPrefetchService.Owner("machine", "source", "hub:/hubs/popular-tv") + ":show:" + PlexPrefetchService.Hash("show-1") + suffix;

    [Fact]
    public void HubOwners_ResolveToTheConfiguredHubTitle()
    {
        var source = PlexPrefetchService.DescribeOwner(HubOwner(), WithSource("Popular TV This Year"), [Server]);
        Assert.Equal(new PrefetchJobSource("Popular TV This Year", "plex-source"), source);
        Assert.Equal("Popular TV This Year · head/tail",
            PlexPrefetchService.DescribeOwner(HubOwner(":minimum"), WithSource("Popular TV This Year"), [Server]).Label);
    }

    [Fact]
    public void RemovedHubOrServer_FallsBackToTheGenericLabel()
    {
        var generic = new PrefetchJobSource("Selected Plex hub/collection", "plex-source");
        Assert.Equal(generic, PlexPrefetchService.DescribeOwner(HubOwner(), new PrefetchSettings(), [Server]));
        Assert.Equal(generic, PlexPrefetchService.DescribeOwner(HubOwner(), WithSource("Popular TV This Year"), []));
    }

    [Theory]
    [InlineData("manual", "Manual", "manual")]
    [InlineData("backfill", "Playback not yet cached", "backfill")]
    [InlineData("finish-watched", "Finish partially watched", "finish-watched")]
    [InlineData("read", "Read activity", "read")]
    [InlineData("plex:s:realtime-next:u:episode:x:unwatched", "Next episode · realtime · Unknown user", "plex-realtime-next")]
    [InlineData("plex:s:history-next:u:episode:x", "Next episode · history · Unknown user", "plex-history-next")]
    [InlineData("plex:s:realtime:u:movie:x", "Playing now", "plex-realtime")]
    [InlineData("plex:s:history:u:movie:x", "Watch history", "plex-history")]
    [InlineData("something-new", "Background warming", "other")]
    public void Owners_MapToLabelAndCategory(string owner, string label, string category) =>
        Assert.Equal(new PrefetchJobSource(label, category), PlexPrefetchService.DescribeOwner(owner, new PrefetchSettings(), [Server]));

    [Fact]
    public void SeveralOwners_AreDistinct_MostSpecificFirst_AndCapped()
    {
        var settings = WithSource("Popular TV This Year");
        var (sources, count) = PlexPrefetchService.DescribeOwners(
            ["manual", HubOwner(), "plex:s:realtime:u:movie:x", "manual", "plex:s2:realtime:u2:movie:y"], settings, [Server], limit: 2);
        Assert.Equal(3, count);
        Assert.Equal(new[] { "Playing now", "Popular TV This Year" }, sources.Select(source => source.Label));
    }

    [Fact]
    public void Predictions_ResolveTheOwningUserAndKeepMultipleViewers()
    {
        var alice = PlexPrefetchService.Owner(Server.Id, "history-next", "alice-id") + ":episode:x:unwatched";
        var bob = PlexPrefetchService.Owner(Server.Id, "realtime-next", "bob-id") + ":episode:x:minimum";
        var names = new Dictionary<string, string>
        {
            [PlexPrefetchService.Hash(Server.Id) + ":" + PlexPrefetchService.Hash("alice-id")] = "Alice",
            [PlexPrefetchService.Hash(Server.Id) + ":" + PlexPrefetchService.Hash("bob-id")] = "Bob",
        };
        var (sources, count) = PlexPrefetchService.DescribeOwners([alice, bob, alice], new(), [Server], userNames: names);
        Assert.Equal(2, count);
        Assert.Contains(sources, source => source.Label == "Next episode · history · Alice");
        Assert.Contains(sources, source => source.Label == "Next episode · realtime · Bob · head/tail");
        Assert.Equal("Next episode · history · Unknown user",
            PlexPrefetchService.DescribeOwner(alice, new(), [Server], new Dictionary<string, string>
            {
                [PlexPrefetchService.Hash("different-server") + ":" + PlexPrefetchService.Hash("alice-id")] = "Wrong user",
            }).Label);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
