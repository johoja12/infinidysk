using Microsoft.Extensions.Hosting;

namespace NzbWebDAV.Services.Prefetch;

public interface IPrefetchExecutor
{
    Task ExecuteAsync(PrefetchJob job, CancellationToken ct);
}

public sealed class PrefetchCoordinator(PrefetchJobStore store, IPrefetchExecutor executor,
    Func<PrefetchSettings> settings, Func<bool> admission) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!admission() || store.Paused) return;
        var tasks = new List<Task>();
        for (var index = 0; index < settings().MaxConcurrentJobs; index++)
        {
            var job = store.ClaimNext();
            if (job is null) break;
            tasks.Add(RunJobAsync(job, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunJobAsync(PrefetchJob job, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _running.Add(job.Id, cancellation);
        try
        {
            if (!store.IsRunning(job.Id) || store.Paused || !admission()) throw new PrefetchDeferredException("Warming is paused or foreground playback has priority.");
            await executor.ExecuteAsync(job, cancellation.Token).ConfigureAwait(false);
            store.Finish(job.Id, true, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { store.Defer(job.Id, "Interrupted; verified coverage retained.", TimeSpan.FromSeconds(30), consumeAttempt: false); }
        catch (PrefetchDeferredException exception)
        { store.Defer(job.Id, exception.Message, TimeSpan.FromMinutes(1), exception.CountsAsFailure); }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        { store.Defer(job.Id, "Source or cache temporarily unavailable; verified coverage retained.", TimeSpan.FromMinutes(1), consumeAttempt: true); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { store.Finish(job.Id, false, "Source or cache operation failed. Check source health and storage availability."); }
        finally { lock (_gate) _running.Remove(job.Id); }
    }

    public void Change(string id, string operation, int? priority = null)
    {
        store.Change(id, operation, priority);
        if (operation is "pause" or "cancel") lock (_gate) _running.GetValueOrDefault(id)?.Cancel();
    }

    public void PruneOwners(Func<string, bool> keep)
    {
        store.PruneOwners(keep);
        lock (_gate)
            foreach (var (id, cancellation) in _running)
                if (!store.IsRunning(id)) cancellation.Cancel();
    }

    public void SetPaused(bool paused)
    {
        store.SetPaused(paused);
        if (paused) lock (_gate) foreach (var cancellation in _running.Values) cancellation.Cancel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
