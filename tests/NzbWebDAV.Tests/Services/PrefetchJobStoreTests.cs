using NzbWebDAV.Services.Prefetch;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchJobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-jobs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DailyBudgetUsed_ReflectsReturnedCredit()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, jobs.GetDailyBudgetUsed());
        Assert.Equal(100, jobs.ReserveDailyCredit(100, 200, day));
        Assert.Equal(100, jobs.GetDailyBudgetUsed());
        jobs.ReturnDailyCredit(40, day);
        Assert.Equal(60, jobs.GetDailyBudgetUsed());
    }

    [Fact]
    public void HasOwner_DistinguishesManualRequestsFromPolicyOwners()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = jobs.Enqueue(item, "plex:hub", 20);
        Assert.False(jobs.HasOwner(job.Id, "manual"));
        Assert.True(jobs.HasOwner(job.Id, "plex:hub"));

        Assert.Equal(job.Id, jobs.Enqueue(item, "manual", 50).Id); // A manual ask joins the queued job.
        Assert.True(jobs.HasOwner(job.Id, "manual"));
    }

    [Fact]
    public void RecordedVerification_MatchesOnlyContainedRangesOfTheSameGenerationSinceCutoff()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.False(jobs.WasVerifiedSince(item, "rev-a", 0, 0, since));

        jobs.RecordVerified(item, "rev-a", 0, 0);
        Assert.True(jobs.WasVerifiedSince(item, "rev-a", 0, 0, since));
        Assert.True(jobs.WasVerifiedSince(item, "rev-a", 100, 50, since)); // A whole-file record contains any range.
        Assert.False(jobs.WasVerifiedSince(item, "rev-b", 0, 0, since)); // A changed source must warm again.
        Assert.False(jobs.WasVerifiedSince(Guid.NewGuid(), "rev-a", 0, 0, since));
        Assert.False(jobs.WasVerifiedSince(item, "rev-a", 0, 0, DateTimeOffset.UtcNow.AddMinutes(1))); // Outside the window.

        var partial = Guid.NewGuid();
        jobs.RecordVerified(partial, "rev-a", 100, 50);
        Assert.True(jobs.WasVerifiedSince(partial, "rev-a", 110, 40, since));
        Assert.False(jobs.WasVerifiedSince(partial, "rev-a", 90, 20, since));
        Assert.False(jobs.WasVerifiedSince(partial, "rev-a", 110, 50, since));
        Assert.False(jobs.WasVerifiedSince(partial, "rev-a", 100, 0, since)); // To end of file exceeds the record.
    }

    [Fact]
    public void BridgingQueuedRanges_CoalescesTransitively_WithOwnersAndPriority()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var first = jobs.Enqueue(item, "source-a", 1, 0, 10);
        jobs.Enqueue(item, "manual", 80, 20, 10);
        jobs.Enqueue(item, "source-c", 3, 40, 10);
        var merged = jobs.EnqueueWithOutcome(item, "bridge", 2, 10, 30);
        Assert.False(merged.Created);
        Assert.Equal(first.Id, merged.Job.Id);
        Assert.Equal(0, merged.Job.Start);
        Assert.Equal(50, merged.Job.Length);
        Assert.Equal(80, merged.Job.Priority);
        Assert.Single(jobs.List());
        jobs.PruneOwners(owner => owner == "manual");
        Assert.Equal(first.Id, jobs.ClaimNext()!.Id);
    }

    [Fact]
    public void BackfillJobs_SurvivePlexPolicyPruning_AndRunFirst()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var watched = Guid.NewGuid();
        var predicted = Guid.NewGuid();
        jobs.Enqueue(predicted, "plex:abc:source:def", 10, 0, 0);
        var backfill = jobs.Enqueue(watched, PrefetchRuntime.BackfillOwner, 60, 4L << 20, 20L << 20);
        jobs.PruneOwners(PlexPrefetchService.IsSystemOwner);
        Assert.Single(jobs.List(), job => job.State == "queued");
        Assert.Equal(backfill.Id, jobs.ClaimNext()!.Id);
        Assert.Equal("Playback not yet cached", PlexPrefetchService.SourceLabel(PrefetchRuntime.BackfillOwner));
    }

    [Fact]
    public void ResumedLegacyRanges_AreMergedTransitivelyRegardlessOfCandidateOrder()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var paused = new List<PrefetchJob>();
        for (var start = 0; start < 30; start += 10)
        {
            var job = jobs.Enqueue(item, "owner-" + start, 0, start, 10);
            jobs.Change(job.Id, "pause");
            paused.Add(job);
        }
        foreach (var job in paused) jobs.Change(job.Id, "resume");
        var merged = jobs.Enqueue(item, "manual", 0, 30, 10);
        Assert.Equal(0, merged.Start);
        Assert.Equal(40, merged.Length);
        Assert.Single(jobs.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongMaximumFiniteRange_IsDistinctFromOpenEndedTail(bool toEof)
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        jobs.Enqueue(item, "manual", 0, long.MaxValue - 20, 20);
        var merged = jobs.Enqueue(item, "tail", 0, long.MaxValue - 10, toEof ? 0 : 10);
        Assert.Equal(long.MaxValue - 20, merged.Start);
        Assert.Equal(toEof ? 0 : 20, merged.Length);
        Assert.Single(jobs.List());
    }

    [Fact]
    public async Task EnqueueOutcomes_AreAtomicAcrossConcurrentRequests()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => jobs.EnqueueWithOutcome(item, "owner-" + index, 0))));
        Assert.Single(outcomes, outcome => outcome.Created);
        Assert.Single(outcomes.Select(outcome => outcome.Job.Id).Distinct());
        Assert.Single(jobs.List());
    }

    [Fact]
    public void EofRangeAtNonzeroStart_MergesAllTouchingQueuedSuccessors()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var first = jobs.Enqueue(item, "manual", 0, 10, 5);
        jobs.Enqueue(item, "source", 0, 30, 10);
        var merged = jobs.Enqueue(item, "tail", 0, 15, 0);
        Assert.Equal(first.Id, merged.Id);
        Assert.Equal(10, merged.Start);
        Assert.Equal(0, merged.Length);
        Assert.Single(jobs.List());
    }

    [Fact]
    public void WholeFileUpgrade_MergesEveryQueuedRange()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        jobs.Enqueue(item, "source-a", 0, 10, 5);
        jobs.Enqueue(item, "source-b", 0, 30, 5);
        var whole = jobs.Enqueue(item, "manual", 50);
        Assert.Equal(0, whole.Start);
        Assert.Equal(0, whole.Length);
        Assert.Single(jobs.List());
        jobs.PruneOwners(owner => owner == "source-b");
        Assert.Equal(whole.Id, jobs.ClaimNext()!.Id);
    }

    [Fact]
    public void OverlappingSuccessor_DoesNotExtendRunningRange()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var running = jobs.Enqueue(item, "manual", 0, 0, 10);
        jobs.ClaimNext();
        jobs.Enqueue(item, "tail", 0, 20, 10);
        var successor = jobs.Enqueue(item, "bridge", 0, 10, 15);
        Assert.Equal(10, successor.Start);
        Assert.Equal(20, successor.Length);
        Assert.Equal(10, jobs.List().Single(job => job.Id == running.Id).Length);
        Assert.Null(jobs.ClaimNext());
        jobs.Finish(running.Id, true, null);
        Assert.Equal(successor.Id, jobs.ClaimNext()!.Id);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("failed")]
    public void InactiveRanges_AreOnlyDeduplicatedExactly(string state)
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var inactive = jobs.Enqueue(item, "manual", 0, 0, 10);
        if (state == "paused") jobs.Change(inactive.Id, "pause");
        else { jobs.ClaimNext(); jobs.Finish(inactive.Id, false, "failed"); }
        var overlap = jobs.EnqueueWithOutcome(item, "source", 0, 5, 10);
        Assert.True(overlap.Created);
        Assert.NotEqual(inactive.Id, overlap.Job.Id);
        Assert.Equal(inactive.Id, jobs.Enqueue(item, "exact", 0, 0, 10).Id);
        Assert.Equal(2, jobs.List().Count);
    }

    [Fact]
    public void MergeOwnerOverflow_RejectsAtomically_AndManualDoesNotUseAutomaticSlot()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        for (var i = 0; i < 64; i++) jobs.Enqueue(item, "a-" + i, 0, 0, 10);
        for (var i = 0; i < 65; i++) jobs.Enqueue(item, "b-" + i, 0, 20, 10);
        Assert.Throws<ArgumentException>(() => jobs.Enqueue(item, "manual", 100, 10, 10));
        Assert.Equal(2, jobs.List().Count);
        Assert.All(jobs.List(), job => Assert.Equal(0, job.Priority));
        var other = Guid.NewGuid();
        jobs.Enqueue(other, "manual", 0);
        for (var i = 0; i < 128; i++) jobs.Enqueue(other, "owner-" + i, 0);
        Assert.Throws<ArgumentException>(() => jobs.Enqueue(other, "excess", 0));
        jobs.PruneOwners(owner => owner == "owner-127");
        Assert.Equal(other, jobs.ClaimNext()!.ItemId);
    }

    [Fact]
    public void MergedIntent_PreservesOldestCreation_MaximumAttempts_AndLatestBackoff()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var jobs = new PrefetchJobStore(path);
        var item = Guid.NewGuid();
        var first = jobs.Enqueue(item, "manual", 0, 0, 10);
        jobs.ClaimNext();
        jobs.Defer(first.Id, "first", TimeSpan.FromHours(1));
        var second = jobs.Enqueue(item, "source", 0, 20, 10);
        jobs.ClaimNext();
        jobs.Defer(second.Id, "second", TimeSpan.FromHours(2));
        using var database = new SqliteConnection("Data Source=" + path);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE Attempts SET Count=3; UPDATE Jobs SET Created=123 WHERE Start=20; SELECT MAX(Until) FROM Deferred";
        var until = (long)command.ExecuteScalar()!;
        var merged = jobs.Enqueue(item, "bridge", 100, 10, 10);
        Assert.Equal(second.Id, merged.Id);
        command.CommandText = "SELECT Created FROM Jobs";
        Assert.Equal(123L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT Count FROM Attempts";
        Assert.Equal(3L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT Until FROM Deferred";
        Assert.Equal(until, (long)command.ExecuteScalar()!);
        Assert.Null(jobs.ClaimNext()); // Old age must not be refreshed by the bridge intent.
        Assert.Equal("failed", jobs.List().Single().State);
    }

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
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 1);
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

    [Fact]
    public void CompletedJob_RecordsTimingAndOnlyTheBytesItWarmed()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var queued = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Null(queued.StartedAt);
        Assert.Null(queued.WarmedBytes);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var running = jobs.ClaimNext()!;
        Assert.InRange(running.StartedAt!.Value, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(0, running.WarmedBytes);

        // Coverage includes bytes that were already cached; warmed bytes count only this job's fills.
        jobs.Progress(running.Id, "g1", 900, 0);
        jobs.Progress(running.Id, "g1", 1_000, 60);
        jobs.Progress(running.Id, "g1", 1_100, 40);
        Thread.Sleep(20);
        jobs.Finish(running.Id, true, null);

        var finished = jobs.List().Single();
        Assert.Equal("completed", finished.State);
        Assert.Equal(1_100, finished.CommittedBytes);
        Assert.Equal(100, finished.WarmedBytes);
        Assert.Equal(running.StartedAt, finished.StartedAt);
        Assert.NotNull(finished.FinishedAt);
        Assert.True(finished.FinishedAt >= finished.StartedAt);
        Assert.InRange(finished.ActiveMs!.Value, 1, finished.FinishedAt!.Value - finished.StartedAt!.Value);
    }

    [Fact]
    public void DeferredTime_IsExcludedFromActiveDurationAndWarmedBytesAccumulateAcrossRuns()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        var firstRun = jobs.ClaimNext()!;
        jobs.Progress(job.Id, "g1", 50, 50);
        jobs.Defer(job.Id, "playback has priority", TimeSpan.Zero, consumeAttempt: false);
        var deferred = jobs.List().Single();
        Assert.Equal("queued", deferred.State);
        Assert.Null(deferred.FinishedAt);
        var activeAfterFirstRun = deferred.ActiveMs!.Value;

        Thread.Sleep(60); // Waiting in the queue must not count as warming time.
        var secondRun = jobs.ClaimNext()!;
        Assert.Equal(firstRun.StartedAt, secondRun.StartedAt);
        jobs.Progress(job.Id, "g1", 80, 30);
        jobs.Finish(job.Id, false, "source failed");

        var failed = jobs.List().Single();
        Assert.Equal("failed", failed.State);
        Assert.Equal(80, failed.WarmedBytes);
        Assert.True(failed.ActiveMs >= activeAfterFirstRun);
        Assert.True(failed.ActiveMs < failed.FinishedAt - failed.StartedAt - 50);
    }

    [Fact]
    public void Retry_StartsAFreshMeasurement()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        jobs.ClaimNext();
        jobs.Progress(job.Id, "g1", 10, 10);
        jobs.Change(job.Id, "cancel");
        var cancelled = jobs.List().Single();
        Assert.NotNull(cancelled.FinishedAt);
        Assert.NotNull(cancelled.ActiveMs);

        jobs.Change(job.Id, "retry");
        var retried = jobs.List().Single();
        Assert.Equal("queued", retried.State);
        Assert.Null(retried.StartedAt);
        Assert.Null(retried.FinishedAt);
        Assert.Null(retried.ActiveMs);
        Assert.Null(retried.WarmedBytes);
    }

    [Fact]
    public void LegacyDatabase_GainsNullableTimingColumnsWithoutLosingHistory()
    {
        var path = Path.Combine(_root, "legacy.db");
        Directory.CreateDirectory(_root);
        using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE Jobs(
                    Id TEXT PRIMARY KEY,ItemId TEXT NOT NULL,Trigger TEXT NOT NULL,Priority INTEGER NOT NULL,
                    State TEXT NOT NULL,Start INTEGER NOT NULL,Length INTEGER NOT NULL,Generation TEXT NULL,
                    CommittedBytes INTEGER NOT NULL DEFAULT 0,Error TEXT NULL,Updated INTEGER NOT NULL,Created INTEGER NOT NULL);
                INSERT INTO Jobs VALUES('old','0123456789abcdef0123456789abcdef','plex:hub',0,'completed',0,0,'g1',500,NULL,1000,1000);
                """;
            create.ExecuteNonQuery();
        }

        using (var jobs = new PrefetchJobStore(path))
        {
            var old = jobs.List().Single();
            Assert.Equal("completed", old.State);
            Assert.Equal(500, old.CommittedBytes);
            Assert.Null(old.StartedAt);
            Assert.Null(old.FinishedAt);
            Assert.Null(old.ActiveMs);
            Assert.Null(old.WarmedBytes);
        }
        using (var reopened = new PrefetchJobStore(path)) Assert.Single(reopened.List()); // Upgrade is idempotent.
    }

    [Fact]
    public void Restart_FoldsAnInterruptedRunIntoActiveDuration()
    {
        var path = Path.Combine(_root, "jobs.db");
        string id;
        using (var jobs = new PrefetchJobStore(path))
        {
            id = jobs.Enqueue(Guid.NewGuid(), "manual", 0).Id;
            jobs.ClaimNext();
            Thread.Sleep(20);
            jobs.Progress(id, "g1", 10, 10);
        }
        using var restored = new PrefetchJobStore(path);
        var job = restored.List().Single();
        Assert.Equal("paused", job.State);
        Assert.True(job.ActiveMs > 0);
        Assert.Equal(10, job.WarmedBytes);
    }

    private const long MiB = 1024L * 1024;

    [Fact]
    public void Backfill_MergesNearbyQueuedRangesButKeepsDistantOnesSeparate()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddHours(-6);
        var first = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 0, 4 * MiB, "g1", 64 * MiB, since);
        var near = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 40 * MiB, 4 * MiB, "g1", 64 * MiB, since);
        Assert.True(first.Created);
        Assert.False(near.Created);
        Assert.Equal(first.Job.Id, near.Job.Id);
        Assert.Equal((0L, 44 * MiB), (near.Job.Start, near.Job.Length));

        var far = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 500 * MiB, 4 * MiB, "g1", 64 * MiB, since);
        Assert.True(far.Created);
        Assert.Equal(2, jobs.List().Count);
    }

    [Fact]
    public void Backfill_ReopensARecentlyCompletedBackfillOfTheSameGeneration()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddHours(-6);
        var completed = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 0, 4 * MiB, "g1", 64 * MiB, since).Job;
        Assert.Equal(completed.Id, jobs.ClaimNext()!.Id);
        jobs.Progress(completed.Id, "g1", 4 * MiB, 4 * MiB);
        jobs.Finish(completed.Id, true, null);

        var reopened = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 8 * MiB, 4 * MiB, "g1", 64 * MiB, since);
        Assert.False(reopened.Created);
        Assert.Equal(completed.Id, reopened.Job.Id);
        Assert.Equal("queued", reopened.Job.State);
        Assert.Equal((0L, 12 * MiB), (reopened.Job.Start, reopened.Job.Length));
        Assert.Null(reopened.Job.FinishedAt);
        Assert.Single(jobs.List()); // One row per session instead of one per skipped block.
    }

    [Theory]
    [InlineData("g2", false)] // The source changed: the old revision's job is history.
    [InlineData("g1", true)] // Completed before the revive window.
    public void Backfill_DoesNotReopenAnotherRevisionOrAnOldJob(string generation, bool expired)
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var old = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 0, 4 * MiB, "g1", 64 * MiB, DateTimeOffset.UtcNow).Job;
        jobs.ClaimNext();
        jobs.Progress(old.Id, "g1", 4 * MiB);
        jobs.Finish(old.Id, true, null);

        var since = expired ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow.AddHours(-6);
        var next = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 4 * MiB, 4 * MiB, generation, 64 * MiB, since);
        Assert.True(next.Created);
        Assert.Equal("completed", jobs.List().Single(job => job.Id == old.Id).State);
    }

    [Fact]
    public void Backfill_DoesNotReopenACompletedJobSharedWithAnotherOwner()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var shared = jobs.Enqueue(item, "manual", 50, 0, 4 * MiB);
        jobs.Enqueue(item, PrefetchRuntime.BackfillOwner, 60, 0, 4 * MiB);
        jobs.ClaimNext();
        jobs.Progress(shared.Id, "g1", 4 * MiB);
        jobs.Finish(shared.Id, true, null);

        var next = jobs.EnqueueBackfill(item, PrefetchRuntime.BackfillOwner, 60, 4 * MiB, 4 * MiB, "g1", 64 * MiB,
            DateTimeOffset.UtcNow.AddHours(-6));
        Assert.True(next.Created);
    }

    [Fact]
    public void FailureCodeAndRemedy_AreStoredAndClearedOnRetry()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 50);
        jobs.ClaimNext();
        jobs.Finish(job.Id, false, "Release damaged on Usenet.", PrefetchFailureCodes.SourceDamaged, PrefetchRemedies.RepairQueued);
        var failed = jobs.List().Single();
        Assert.Equal(PrefetchFailureCodes.SourceDamaged, failed.FailureCode);
        Assert.Equal(PrefetchRemedies.RepairQueued, failed.Remedy);

        jobs.Change(job.Id, "retry");
        var retried = jobs.List().Single();
        Assert.Null(retried.FailureCode);
        Assert.Null(retried.Remedy);
    }

    [Fact]
    public void Defer_ReportsWhenTheLastRetryFailsTheJob()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"),
            settings: () => new PrefetchSettings { MaxRetries = 1 });
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 50);
        jobs.ClaimNext();
        Assert.False(jobs.Defer(job.Id, "Cache storage error.", TimeSpan.Zero, failureCode: PrefetchFailureCodes.CacheStorage));
        Assert.Equal(1, jobs.Attempts(job.Id));
        Assert.Equal(PrefetchFailureCodes.CacheStorage, jobs.List().Single().FailureCode);
        Assert.NotNull(jobs.ClaimNext());
        Assert.True(jobs.Defer(job.Id, "Cache storage error.", TimeSpan.Zero, failureCode: PrefetchFailureCodes.CacheStorage));
        Assert.Equal("failed", jobs.List().Single().State);
    }

    [Fact]
    public void DamagedVerdict_HoldsUntilExpiryOrANewRevision()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.False(jobs.IsDamaged(item, "g1", now));

        jobs.MarkDamaged(item, "g1", now.AddHours(24));
        Assert.True(jobs.IsDamaged(item, "g1", now));
        Assert.True(jobs.IsDamaged(item, null, now)); // Unknown revision keeps the cooldown.
        Assert.False(jobs.IsDamaged(item, "g1", now.AddHours(25))); // Expired verdicts clear.
        Assert.False(jobs.IsDamaged(item, "g1", now)); // ...and stay cleared.

        jobs.MarkDamaged(item, "g1", now.AddHours(24));
        Assert.False(jobs.IsDamaged(item, "g2", now)); // Repair changed the revision.
        Assert.False(jobs.IsDamaged(item, "g1", now));
    }

    [Fact]
    public void ExistingDatabase_GainsFailureColumnsAdditively()
    {
        var path = Path.Combine(_root, "jobs.db");
        Directory.CreateDirectory(_root);
        using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE Jobs(Id TEXT PRIMARY KEY,ItemId TEXT NOT NULL,Trigger TEXT NOT NULL,Priority INTEGER NOT NULL,
                    State TEXT NOT NULL,Start INTEGER NOT NULL,Length INTEGER NOT NULL,Generation TEXT NULL,
                    CommittedBytes INTEGER NOT NULL DEFAULT 0,Error TEXT NULL,Updated INTEGER NOT NULL,Created INTEGER NOT NULL);
                INSERT INTO Jobs VALUES('legacy','00000000000000000000000000000001','manual',1,'failed',0,0,NULL,0,'Old failure',1,1);
                """;
            create.ExecuteNonQuery();
        }
        using var jobs = new PrefetchJobStore(path);
        var job = Assert.Single(jobs.List());
        Assert.Equal("Old failure", job.Error);
        Assert.Null(job.FailureCode);
        Assert.Null(job.Remedy);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
