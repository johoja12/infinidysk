using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Exceptions;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-runner-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("claim")]
    [InlineData("disposed")]
    public async Task UnavailableMetadata_DisablesWarmingWithoutEscapingOrRepeatedAdmission(string failure)
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        if (failure == "claim") ExecuteSql(path, "DROP TABLE Jobs");
        else store.Dispose();
        var admitted = 0;
        using var coordinator = new PrefetchCoordinator(store, new FailingExecutor(), () => new(), () => { admitted++; return true; });
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(coordinator.RuntimeError);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCompletionOrDeferTransition_IsContainedWithoutClaimingSuccess(bool sourceFailure)
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new CallbackExecutor((_, _) =>
        {
            ExecuteSql(path, "CREATE TRIGGER FailTransitions BEFORE UPDATE ON Jobs BEGIN SELECT RAISE(ABORT,'private-metadata-error'); END");
            return sourceFailure ? Task.FromException(new IOException("private-source-error")) : Task.CompletedTask;
        });
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(coordinator.RuntimeError);
        Assert.DoesNotContain("private", coordinator.RuntimeError);
        Assert.Equal("running", Assert.Single(store.List()).State);
        ExecuteSql(path, "DROP TRIGGER FailTransitions; DROP TABLE State");
        await coordinator.RunOnceAsync(CancellationToken.None); // A latched fault must not touch failed metadata again.
        Assert.Equal(0, Assert.Single(store.List()).CommittedBytes);
    }

    [Fact]
    public async Task ClaimFailure_CancelsAlreadyStartedSibling_AndHostedLoopStopsCleanly()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new CallbackExecutor(async (_, ct) =>
        {
            ExecuteSql(path, "DROP TABLE Jobs");
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); }
        });
        using var coordinator = new PrefetchCoordinator(store, executor, () => new() { MaxConcurrentJobs = 2 }, () => true);
        await coordinator.StartAsync(CancellationToken.None);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(coordinator.RuntimeError);
        Assert.False(coordinator.ExecuteTask!.IsCompleted);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync(stop.Token);
        Assert.True(coordinator.ExecuteTask.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("foreign-cancellation")]
    [InlineData("disposal")]
    [InlineData("late-success")]
    public async Task ApplicationStopping_BeforeWorkerStop_RetainsCoverageWithoutRetryOrFurtherClaims(string outcome)
    {
        using var applicationStopping = new CancellationTokenSource();
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"),
            settings: () => new() { MaxRetries = 0 });
        var first = store.Enqueue(Guid.NewGuid(), "manual", 10);
        var second = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var calls = 0;
        var executor = new CallbackExecutor((job, _) =>
        {
            calls++;
            store.Progress(job.Id, "generation", 4096);
            applicationStopping.Cancel(); // The hosted worker's StopAsync has not run yet.
            return outcome switch
            {
                "foreign-cancellation" => Task.FromException(new OperationCanceledException(new CancellationToken(true))),
                "disposal" => Task.FromException(new ObjectDisposedException("source")),
                _ => Task.CompletedTask
            };
        });
        using var coordinator = new PrefetchCoordinator(store, executor,
            () => new() { MaxConcurrentJobs = 2 }, () => true, applicationStopping: applicationStopping.Token);
        await coordinator.RunOnceAsync(CancellationToken.None);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Null(coordinator.RuntimeError);
        var interrupted = store.List().Single(job => job.Id == first.Id);
        Assert.Equal("queued", interrupted.State); // MaxRetries=0 would fail if interruption consumed an attempt.
        Assert.Equal(4096, interrupted.CommittedBytes);
        Assert.Contains("Interrupted", interrupted.Error);
        Assert.Equal("queued", store.List().Single(job => job.Id == second.Id).State);
    }

    [Fact]
    public async Task ApplicationStopping_CancelsInFlightIoBeforeHostedStop()
    {
        using var applicationStopping = new CancellationTokenSource();
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true,
            applicationStopping: applicationStopping.Token);
        var run = coordinator.RunOnceAsync(CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        applicationStopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("queued", Assert.Single(store.List()).State);
        Assert.Null(coordinator.RuntimeError);
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var database = new SqliteConnection("Data Source=" + path);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task ShutdownCancellation_IsClean_AndOutOfMemoryIsNotHidden()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        using var coordinator = new PrefetchCoordinator(store,
            new CallbackExecutor((_, _) => Task.FromException(new OutOfMemoryException("injected"))), () => new(), () => true);
        await coordinator.RunOnceAsync(new CancellationToken(canceled: true));
        Assert.Null(coordinator.RuntimeError);
        Assert.Equal("queued", Assert.Single(store.List()).State);
        await Assert.ThrowsAsync<OutOfMemoryException>(() => coordinator.RunOnceAsync(CancellationToken.None));
        Assert.Null(coordinator.RuntimeError);
    }

    private sealed class CallbackExecutor(Func<PrefetchJob, CancellationToken, Task> callback) : IPrefetchExecutor
    {
        public Task ExecuteAsync(PrefetchJob job, CancellationToken ct) => callback(job, ct);
    }

    [Theory]
    [InlineData(0, "failed")]
    [InlineData(1, "queued")]
    public async Task TransientSourceFailure_UsesConfiguredBoundedRetry(int retries, string expected)
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"), settings: () => new() { MaxRetries = retries });
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        using var coordinator = new PrefetchCoordinator(store, new FailingExecutor(), () => new(), () => true);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(expected, Assert.Single(store.List()).State);
    }

    [Theory]
    [InlineData(180, 170, 190)]
    [InlineData(null, 0, 2)]
    public async Task Deferral_RetriesAfterTheRequestedDelay(int? retryMinutes, int minMinutes, int maxMinutes)
    {
        var path = Path.Combine(_root, "jobs-retry.db");
        using var store = new PrefetchJobStore(path);
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var retry = retryMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
        using var coordinator = new PrefetchCoordinator(store,
            new CallbackExecutor((_, _) => throw new PrefetchDeferredException("Budget used up.", retryAfter: retry)), () => new(), () => true);
        var before = DateTimeOffset.UtcNow;
        await coordinator.RunOnceAsync(CancellationToken.None);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Until FROM Deferred WHERE Id=$id";
        command.Parameters.AddWithValue("$id", job.Id);
        var until = DateTimeOffset.FromUnixTimeMilliseconds((long)command.ExecuteScalar()!);
        Assert.InRange(until - before, TimeSpan.FromMinutes(minMinutes), TimeSpan.FromMinutes(maxMinutes));
        Assert.Equal("queued", Assert.Single(store.List()).State);
    }

    [Fact]
    public async Task CircuitRejection_RetainsCoverageAndResumesAfterBackoff()
    {
        var path = Path.Combine(_root, "circuit-recovery.db");
        using var store = new PrefetchJobStore(path);
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var calls = 0;
        using var coordinator = new PrefetchCoordinator(store, new CallbackExecutor((current, _) =>
        {
            calls++;
            if (calls == 1)
            {
                store.Progress(current.Id, "generation", 4096);
                throw new CircuitAdmissionRejectedException();
            }
            Assert.Equal(4096, current.CommittedBytes);
            return Task.CompletedTask;
        }), () => new(), () => true);
        await coordinator.RunOnceAsync(CancellationToken.None);
        var saved = Assert.Single(store.List());
        Assert.Equal("queued", saved.State);
        Assert.Equal("provider-unavailable", saved.FailureCode);
        Assert.Equal(4096, saved.CommittedBytes);
        Assert.Equal(1, store.Attempts(job.Id));
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, calls); // No immediate retry during the cooldown.
        ExecuteSql(path, "UPDATE Deferred SET Until=0");
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal("completed", Assert.Single(store.List()).State);
        Assert.Null(coordinator.RuntimeError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task CircuitRejection_BackoffIsCappedAndRetryLimitIsHonored(int maxRetries)
    {
        var path = Path.Combine(_root, "circuit-limit.db");
        using var store = new PrefetchJobStore(path, settings: () => new() { MaxRetries = maxRetries });
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var calls = 0;
        using var coordinator = new PrefetchCoordinator(store, new CallbackExecutor((_, _) =>
        {
            calls++;
            throw new CircuitAdmissionRejectedException();
        }), () => new(), () => true);
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            // Deferred timestamps are persisted at millisecond precision.
            var before = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await coordinator.RunOnceAsync(CancellationToken.None);
            Assert.Equal(attempt + 1, calls);
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Until FROM Deferred WHERE Id=$id";
            command.Parameters.AddWithValue("$id", job.Id);
            var until = DateTimeOffset.FromUnixTimeMilliseconds((long)command.ExecuteScalar()!);
            var delay = TimeSpan.FromMinutes(Math.Min(5, Math.Pow(2, Math.Min(3, attempt))));
            Assert.InRange(until - before, delay, delay + TimeSpan.FromSeconds(5));
            await coordinator.RunOnceAsync(CancellationToken.None);
            Assert.Equal(attempt + 1, calls);
            ExecuteSql(path, "UPDATE Deferred SET Until=0");
        }
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(maxRetries + 1, calls);
        var saved = Assert.Single(store.List());
        Assert.Equal("failed", saved.State);
        Assert.Equal("provider-unavailable", saved.FailureCode);
        Assert.Equal(maxRetries + 1, store.Attempts(job.Id));
    }

    private sealed class FailingExecutor : IPrefetchExecutor
    {
        public Task ExecuteAsync(PrefetchJob job, CancellationToken ct) => throw new IOException("Temporary source failure");
    }

    [Fact]
    public async Task Cancellation_InterruptsWorkerAndCannotBecomeCompleted()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true);
        var run = coordinator.RunOnceAsync(CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Change(job.Id, "cancel");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", store.List().Single().State);
    }

    [Fact]
    public async Task RemovingLastSource_CancelsActiveIoButRetainsManualOwnership()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = store.Enqueue(item, "source", 0);
        store.Enqueue(item, "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true);
        var run = coordinator.RunOnceAsync(CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.PruneOwners(owner => owner == "manual");
        Assert.Equal("running", store.List().Single().State);
        coordinator.PruneOwners(_ => false);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", store.List().Single().State);
    }

    [Fact]
    public async Task AdmissionPressure_DoesNotClaimQueuedWork()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => false);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.False(executor.Started.Task.IsCompleted);
        Assert.Equal("queued", store.List().Single().State);
    }

    [Fact]
    public async Task SmallRangeJob_RunsBesideLongWholeFileWarm_LargeRangeWaits()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var whole = store.Enqueue(Guid.NewGuid(), "source", 100);
        var large = store.Enqueue(Guid.NewGuid(), "backfill", 60, 4 * 1024 * 1024, PrefetchCoordinator.ExpressRangeBytes + 4 * 1024 * 1024);
        var small = store.Enqueue(Guid.NewGuid(), "backfill", 60, 4 * 1024 * 1024, 4 * 1024 * 1024);
        var smallDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new CallbackExecutor(async (job, ct) =>
        {
            if (job.Id == whole.Id) await smallDone.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            else if (job.Id == small.Id) smallDone.SetResult();
            else throw new InvalidOperationException("A large range job must wait for a regular slot.");
        });
        using var coordinator = new PrefetchCoordinator(store, executor, () => new() { MaxConcurrentJobs = 1 }, () => true)
            { ExpressPollInterval = TimeSpan.FromMilliseconds(10) };
        await coordinator.RunOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var states = store.List().ToDictionary(job => job.Id, job => job.State);
        Assert.Equal("completed", states[whole.Id]);
        Assert.Equal("completed", states[small.Id]);
        Assert.Equal("queued", states[large.Id]);
    }

    private sealed class BlockingExecutor : IPrefetchExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(PrefetchJob job, CancellationToken ct)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
