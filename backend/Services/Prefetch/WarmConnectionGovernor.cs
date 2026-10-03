using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Models;
using Serilog;

namespace NzbWebDAV.Services.Prefetch;

/// <summary>
/// The connection budget of one warming job. Warming may use transfer capacity nobody else
/// needs: the budget follows free provider capacity, keeps a reserve for playback, and drops
/// to the floor as soon as any provider has transfers waiting (playback or imports queued).
/// Shrinking never interrupts a transfer; held permits drain before new ones are granted.
/// </summary>
public sealed class WarmConnectionGovernor : IAsyncDisposable
{
    /// <summary>Upper bound for one job, whatever the pool offers.</summary>
    public const int Ceiling = 24;
    /// <summary>Share of the total transfer budget warming always leaves free.</summary>
    public const int ReservePercent = 25;

    private readonly int _floor;
    private readonly Func<IReadOnlyList<ProviderConnectionSnapshot>> _snapshots;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public WarmConnectionGovernor(int floor, Func<IReadOnlyList<ProviderConnectionSnapshot>> snapshots, TimeSpan interval)
    {
        _floor = Math.Clamp(floor, 1, Ceiling);
        _snapshots = snapshots;
        Semaphore = new PrioritizedSemaphore(_floor, _floor);
        Allowed = _floor;
        try { Adjust(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Log.Debug(exception, "Warming connection budget starts at its floor"); }
        _loop = RunAsync(interval);
    }

    public PrioritizedSemaphore Semaphore { get; }

    /// <summary>Connections the job may currently hold.</summary>
    public int Allowed { get; private set; }

    /// <summary>
    /// The budget for a job holding <paramref name="held"/> connections: what it holds plus the
    /// free transfer capacity above the playback reserve, within [floor, ceiling]; the floor
    /// whenever any provider has waiting transfers.
    /// </summary>
    public static int Target(int floor, int held, IReadOnlyList<ProviderConnectionSnapshot> providers)
    {
        var total = 0;
        var free = 0;
        foreach (var provider in providers)
        {
            if (provider.Admission is { } admission)
            {
                if (admission.WaitingTransferOperations > 0) return floor;
                total += admission.EffectiveTransferLimit;
                free += Math.Max(0, admission.EffectiveTransferLimit - admission.ActiveTransferOperations);
            }
            else
            {
                total += provider.EffectiveMaxConnections;
                free += Math.Max(0, provider.AvailableConnections);
            }
        }
        var reserve = (total * ReservePercent + 99) / 100;
        return Math.Clamp(held + free - reserve, floor, Math.Max(floor, Ceiling));
    }

    private void Adjust()
    {
        var target = Target(_floor, Semaphore.EnteredCount, _snapshots());
        if (target == Allowed) return;
        Allowed = target;
        Semaphore.UpdateMaxAllowed(target);
    }

    private async Task RunAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try { Adjust(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A failed snapshot keeps the last budget; the floor is never at risk.
                    Log.Debug(exception, "Warming connection budget could not be recalculated");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
        Semaphore.Dispose();
    }
}
