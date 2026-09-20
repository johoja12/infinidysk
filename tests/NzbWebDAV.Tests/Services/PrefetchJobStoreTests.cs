using NzbWebDAV.Services.Prefetch;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchJobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-jobs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WholeFileRequestDuringPartialWarm_DoesNotRunSameMediaTwiceConcurrently()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var partial = jobs.Enqueue(item, "realtime", 10, 0, 4);
        Assert.Equal(partial.Id, jobs.ClaimNext()!.Id);
        var whole = jobs.Enqueue(item, "manual", 100);
        var other = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Equal(other.Id, jobs.ClaimNext()!.Id);
        Assert.Null(jobs.ClaimNext());
        jobs.Finish(partial.Id, true, null);
        Assert.Equal(whole.Id, jobs.ClaimNext()!.Id);
    }

    [Fact]
    public void ConfiguredQueueAndRetryLimits_ApplyWithoutRestart()
    {
        var settings = new PrefetchSettings { QueueCapacity = 1, MaxRetries = 0 };
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"), settings: () => settings);
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Throws<ArgumentException>(() => jobs.Enqueue(Guid.NewGuid(), "manual", 0));
        settings = settings with { QueueCapacity = 2 };
        jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Equal(job.Id, jobs.ClaimNext()!.Id);
        jobs.Defer(job.Id, "failure", TimeSpan.Zero);
        Assert.Equal("failed", jobs.List().Single(item => item.Id == job.Id).State);
    }

    [Fact]
    public void PolicyWaits_DoNotConsumeFailureRetries()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        for (var index = 0; index < 10; index++)
        {
            Assert.NotNull(jobs.ClaimNext());
            jobs.Defer(job.Id, "Playback has priority", TimeSpan.Zero, consumeAttempt: false);
        }
        Assert.Equal("queued", jobs.List().Single().State);
    }

    [Fact]
    public void FailedOwnerInsertion_RollsBackManualJob()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var jobs = new PrefetchJobStore(path);
        using var inspection = new SqliteConnection("Data Source=" + path);
        inspection.Open();
        using var trigger = inspection.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER RejectOwner BEFORE INSERT ON Owners BEGIN SELECT RAISE(ABORT,'injected owner failure'); END;";
        trigger.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => jobs.Enqueue(Guid.NewGuid(), "manual", 0));
        Assert.Empty(jobs.List());
    }

    [Fact]
    public void FullFileRequest_SupersedesQueuedPartialWithBothOwners()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var partial = jobs.Enqueue(item, "plex:server:minimum", 1, 4096, 4096);
        var full = jobs.Enqueue(item, "manual", 5);
        Assert.Equal(partial.Id, full.Id);
        Assert.Equal(0, full.Start);
        Assert.Equal(0, full.Length);
        Assert.Single(jobs.List());
        jobs.PruneOwners(owner => owner != "manual");
        Assert.NotNull(jobs.ClaimNext());
    }

    [Fact]
    public void DeferredWorkAndDailyBudget_SurviveRestart()
    {
        var path = Path.Combine(_root, "jobs.db");
        using (var jobs = new PrefetchJobStore(path))
        {
            var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
            jobs.ClaimNext();
            jobs.Defer(job.Id, "No writable folder", TimeSpan.FromHours(1));
            Assert.Null(jobs.ClaimNext());
            Assert.True(jobs.TrySpendDailyBudget(60, 100));
            Assert.False(jobs.TrySpendDailyBudget(50, 100));
        }
        using var reopened = new PrefetchJobStore(path);
        Assert.Null(reopened.ClaimNext());
        Assert.True(reopened.TrySpendDailyBudget(40, 100));
        Assert.False(reopened.TrySpendDailyBudget(1, 100));
    }

    [Fact]
    public void Queue_DeduplicatesBoundsAndPersistsInterruptedWork()
    {
        var path = Path.Combine(_root, "jobs.db");
        string id;
        var item = Guid.NewGuid();
        using (var jobs = new PrefetchJobStore(path, 2))
        {
            id = jobs.Enqueue(item, "manual", 5).Id;
            Assert.Equal(id, jobs.Enqueue(item, "plex-session", 10).Id);
            jobs.Enqueue(Guid.NewGuid(), "manual", 0);
            Assert.Throws<ArgumentException>(() => jobs.Enqueue(Guid.NewGuid(), "manual", 0));
            var active = jobs.ClaimNext();
            Assert.Equal(id, active!.Id);
            jobs.Progress(id, "source-v1", 123);
        }
        using var reopened = new PrefetchJobStore(path, 2);
        Assert.Null(reopened.ClaimNext());
        reopened.Change(id, "resume");
        var resumed = reopened.ClaimNext();
        Assert.Equal(id, resumed!.Id);
        Assert.Equal(123, resumed.CommittedBytes);
        Assert.Equal("source-v1", resumed.Generation);
    }

    [Fact]
    public void SharedIntent_RetainsManualOwnerWhenAutomaticSourceIsDisabled()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = jobs.Enqueue(item, "plex:server:history", 1);
        Assert.Equal(job.Id, jobs.Enqueue(item, "manual", 5).Id);
        jobs.PruneOwners(owner => owner == "manual");
        Assert.Equal(job.Id, jobs.ClaimNext()!.Id);
        jobs.PruneOwners(_ => false);
        Assert.Equal("cancelled", jobs.List().Single().State);
    }

    [Fact]
    public void RetryIsBoundedAndRefreshDoesNotResetAttempts()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = jobs.Enqueue(item, "manual", 1);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(job.Id, jobs.ClaimNext()!.Id);
            jobs.Defer(job.Id, "unavailable", TimeSpan.Zero);
            if (attempt < 3) Assert.Equal(job.Id, jobs.Enqueue(item, "manual", 1).Id);
        }
        Assert.Null(jobs.ClaimNext());
        Assert.Equal("failed", jobs.List().Single().State);
    }

    [Fact]
    public void CancelledJob_CannotBeCompletedByLateWorker()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        jobs.ClaimNext();
        jobs.Change(job.Id, "cancel");
        jobs.Finish(job.Id, true, null);
        Assert.Equal("cancelled", jobs.List().Single().State);
        jobs.Change(job.Id, "retry");
        Assert.Equal("queued", jobs.List().Single().State);
    }

    [Fact]
    public void PausedQueue_DoesNotClaimWork_AndPriorityIsStable()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        var first = jobs.Enqueue(Guid.NewGuid(), "manual", 5);
        jobs.SetPaused(true);
        Assert.Null(jobs.ClaimNext());
        jobs.SetPaused(false);
        Assert.Equal(first.Id, jobs.ClaimNext()!.Id);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
