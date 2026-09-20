using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-runner-" + Guid.NewGuid().ToString("N"));

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
