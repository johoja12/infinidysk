using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PredictionSnapshotTests
{
    [Fact]
    public async Task CoalescesSlowRefreshesAndReturnsSavedRowsImmediately()
    {
        var clock = new Clock();
        var first = new TaskCompletionSource<PredictionRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<PredictionRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new PredictionSnapshotCache(() => "one", _ => Interlocked.Increment(ref calls) == 1 ? first.Task : second.Task, clock, CancellationToken.None);
        Assert.False(cache.Get().HasSnapshot);
        await Eventually(() => calls == 1);
        for (var i = 0; i < 20; i++) Assert.True(cache.Get().Refreshing);
        Assert.Equal(1, calls);
        first.SetResult(new([Candidate()], true, null));
        await Eventually(() => cache.Get().HasSnapshot && !cache.Get().Refreshing);
        var original = cache.Get().UpdatedAt;
        clock.Now += TimeSpan.FromMinutes(2);
        var refreshing = cache.Get();
        Assert.Single(refreshing.Predictions);
        Assert.Equal(original, refreshing.UpdatedAt);
        Assert.True(refreshing.Refreshing);
        second.SetResult(new([], false, "Policy busy"));
        await Eventually(() => cache.Get().Error is not null);
        var stale = cache.Get();
        Assert.Single(stale.Predictions);
        Assert.True(stale.Stale);
        Assert.Equal(original, stale.UpdatedAt);
    }

    [Fact]
    public async Task ConfigurationChangesDiscardOldAttributionAndIgnoreOldInflightRefresh()
    {
        var pending = new TaskCompletionSource<PredictionRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<PredictionRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = "old-account";
        var calls = 0;
        var cache = new PredictionSnapshotCache(() => revision, _ => { return Interlocked.Increment(ref calls) == 1 ? pending.Task : current.Task; }, new Clock(), CancellationToken.None);
        cache.Get();
        await Eventually(() => calls == 1);
        revision = "new-account";
        Assert.Empty(cache.Get().Predictions);
        pending.SetResult(new([Candidate()], true, null));
        await Eventually(() => { cache.Get(); return calls >= 2; });
        Assert.False(cache.Get().HasSnapshot);
        Assert.Empty(cache.Get().Predictions);
        current.SetResult(new([], true, null));
        await Eventually(() => cache.Get().HasSnapshot);
        Assert.Empty(cache.Get().Predictions);
    }

    [Fact]
    public async Task SuccessfulEmptyResultsReplaceSavedRowsButFailuresDoNotCreateAnEmptySuccess()
    {
        var clock = new Clock();
        var next = new PredictionRefresh([Candidate()], true, null);
        var cache = new PredictionSnapshotCache(() => "one", _ => Task.FromResult(next), clock, CancellationToken.None);
        cache.Get();
        await Eventually(() => cache.Get().HasSnapshot);
        next = new([], true, null);
        clock.Now += TimeSpan.FromMinutes(2);
        cache.Get();
        await Eventually(() => cache.Get().HasSnapshot && cache.Get().Predictions.Count == 0);
        Assert.Null(cache.Get().Error);
        var failed = new PredictionSnapshotCache(() => "one", _ => throw new ArgumentException("Busy"), clock, CancellationToken.None);
        failed.Get();
        await Eventually(() => failed.Get().Error is not null);
        Assert.False(failed.Get().HasSnapshot);
    }

    [Fact]
    public async Task RestartRestoresTimestampAndRowsDuringOutageThenRecovers()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "predictions.json");
        try
        {
            var clock = new Clock();
            var candidate = Candidate() with { Viewer = "Alice", ServerName = "Living room" };
            var original = new PredictionSnapshotCache(() => "one", _ => Task.FromResult(new PredictionRefresh([candidate], true, null)),
                clock, CancellationToken.None, new(path));
            original.Get();
            await Eventually(() => original.Get().HasSnapshot && !original.Get().Refreshing);
            var timestamp = original.Get().UpdatedAt;
            clock.Now += TimeSpan.FromSeconds(10);
            var next = new PredictionRefresh([], false, "Timed out", true);
            var restarted = new PredictionSnapshotCache(() => "one", _ => Task.FromResult(next), clock, CancellationToken.None, new(path));
            var restored = restarted.Get();
            Assert.True(restored.HasSnapshot);
            Assert.True(restored.Stale);
            Assert.Equal(timestamp, restored.UpdatedAt);
            Assert.Equal("Alice", Assert.Single(restored.Predictions).Viewer);
            await Eventually(() => restarted.Get().PlexUnavailable);
            Assert.Equal(candidate, Assert.Single(new PredictionSnapshotStore(path).Load("one")!.Predictions));
            next = new([], true, null);
            clock.Now += TimeSpan.FromSeconds(31);
            restarted.Get();
            await Eventually(() => restarted.Get().Predictions.Count == 0 && restarted.Get().Error is null);
            Assert.False(restarted.Get().PlexUnavailable);
            Assert.False(restarted.Get().Stale);
            Assert.Empty(new PredictionSnapshotStore(path).Load("one")!.Predictions);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SavedResultsAreIgnoredAfterAccountChangeAndCorruptFilesAreNonfatal()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "predictions.json");
        try
        {
            var store = new PredictionSnapshotStore(path);
            store.Save(new(1, "old-account", new Clock().Now, [Candidate()]));
            Assert.Null(store.Load("new-account"));
            var cache = new PredictionSnapshotCache(() => "new-account", _ => throw new InvalidOperationException(),
                new Clock(), new CancellationToken(true), store);
            Assert.False(cache.Get().HasSnapshot);
            File.WriteAllText(path, "{broken");
            Assert.Null(store.Load("old-account"));
            Assert.NotNull(store.Warning);
            File.WriteAllText(path, new string('x', 2 * 1024 * 1024 + 1));
            Assert.Null(store.Load("old-account"));
            Assert.NotNull(store.Warning);
            store.Save(new(1, "new-account", new Clock().Now, []));
            Assert.NotNull(store.Load("new-account"));
            Assert.Null(store.Warning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedPersistenceKeepsSuccessfulInMemoryResultsAndReportsWarning()
    {
        var blocker = Path.GetTempFileName();
        try
        {
            var cache = new PredictionSnapshotCache(() => "one", _ => Task.FromResult(new PredictionRefresh([Candidate()], true, null)),
                new Clock(), CancellationToken.None, new(Path.Combine(blocker, "predictions.json")));
            cache.Get();
            await Eventually(() => cache.Get().HasSnapshot);
            Assert.Single(cache.Get().Predictions);
            Assert.Null(cache.Get().Error);
            Assert.NotNull(cache.Get().Warning);
        }
        finally { File.Delete(blocker); }
    }

    private static PrefetchPrediction Candidate() => new(Guid.NewGuid(), "Episode", "History", "Next", 0, 10, 100);
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-05T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
