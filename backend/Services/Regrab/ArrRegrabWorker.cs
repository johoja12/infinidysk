using Microsoft.Extensions.Hosting;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services.Regrab;

/// <summary>
/// Drives <see cref="ArrRegrabService"/> in the background: records regrabs for failed
/// migration imports, processes pending requests one at a time with a pause between Arr
/// operations, and notices when a regrabbed item has been replaced. Requests live in the
/// database, so the worker resumes where it stopped after a restart.
/// </summary>
public sealed class ArrRegrabWorker(ArrRegrabService regrab, MigrationFailureFeed migrationFeed) : BackgroundService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PauseBetweenRequests = TimeSpan.FromSeconds(10);
    private const int RequestsPerTick = 3;
    private static readonly TimeSpan MigrationPollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReplacementPollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastMigrationPoll = DateTimeOffset.MinValue;
        var lastReplacementPoll = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                if (now - lastMigrationPoll >= MigrationPollInterval)
                {
                    lastMigrationPoll = now;
                    await RecordMigrationFailuresAsync(stoppingToken).ConfigureAwait(false);
                }

                for (var i = 0; i < RequestsPerTick; i++)
                {
                    if (!await regrab.ProcessNextDueAsync(stoppingToken).ConfigureAwait(false))
                        break;
                    await Task.Delay(PauseBetweenRequests, stoppingToken).ConfigureAwait(false);
                }

                if (now - lastReplacementPoll >= ReplacementPollInterval)
                {
                    lastReplacementPoll = now;
                    await regrab.CheckReplacementsAsync(10, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                e.LogWarningKnownOrStack("Arr regrab worker pass failed; it will retry on the next pass.");
            }
        }
    }

    internal async Task RecordMigrationFailuresAsync(CancellationToken ct)
    {
        try
        {
            var failures = await migrationFeed.CollectNewAsync(ct).ConfigureAwait(false);
            if (failures.Count > 0)
                await regrab.EnqueueMigrationFailuresAsync(failures, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warning("Could not read the migration package to record regrabs for failed imports. Reason: {Reason}",
                e.Message);
            Log.Debug(e, "Migration regrab feed failure stack");
        }
    }
}
