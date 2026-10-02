using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>A damaged file the imported-content sweep found and queued for repair.</summary>
public sealed record ImportedContentSweepFinding(Guid DavItemId, string Path, string Detail, DateTimeOffset FoundAt);

/// <summary>Progress and results of the one-time imported-content sweep (#130).</summary>
public sealed record ImportedContentSweepState
{
    public string Status { get; init; } = ImportedContentSweepService.StatusIdle;
    /// <summary>Category folder the sweep is limited to, or null for every imported file.</summary>
    public string? Category { get; init; }
    /// <summary>Maximum files to verify before pausing; null for no limit.</summary>
    public int? Limit { get; init; }
    /// <summary>Resume point: the last processed DavItem path (sweeps run in path order).</summary>
    public string? Cursor { get; init; }
    public int Checked { get; init; }
    public int Healthy { get; init; }
    public int Damaged { get; init; }
    public int RepairsQueued { get; init; }
    public int Unverifiable { get; init; }
    public int Skipped { get; init; }
    /// <summary>Files checked during the current run (the limit applies to this count).</summary>
    public int CheckedThisRun { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<ImportedContentSweepFinding> RecentFindings { get; init; } = [];
}

/// <summary>
/// One-time, resumable, low-priority sweep over already-imported library files that runs the
/// sampled yEnc-header verification (<see cref="ArticleContentVerifier"/>) and queues repair
/// for damaged files through <see cref="HealthCheckService.QueueRepairForDamagedReleaseAsync"/>.
/// Files are processed one at a time in path order with a small article concurrency, under the
/// health-check connection gate at background priority, so queue imports and playback keep
/// their connections. Progress persists under the config directory so a restart resumes
/// where the sweep stopped. Only imported media files are verified.
/// </summary>
public sealed class ImportedContentSweepService : BackgroundService
{
    public const string StatusIdle = "idle";
    public const string StatusRunning = "running";
    public const string StatusPaused = "paused";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    internal const int ArticleConcurrency = 2;
    private const int BatchSize = 50;
    private const int MaxRecentFindings = 50;
    private const int ProgressLogInterval = 250;
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HealthCheckService _healthCheckService;
    private readonly HealthCheckConnectionGate _healthCheckConnectionGate;
    private readonly IDbContextFactory<DavDatabaseContext>? _dbContextFactory;
    private readonly Lock _lock = new();
    private ImportedContentSweepState _state;
    private CancellationTokenSource? _runCts;
    private Task _runTask = Task.CompletedTask;
    private CancellationToken _stoppingToken = CancellationToken.None;

    public ImportedContentSweepService(
        HealthCheckService healthCheckService,
        HealthCheckConnectionGate healthCheckConnectionGate,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory = null)
        : this(healthCheckService, healthCheckConnectionGate, dbContextFactory, statePath: null)
    {
    }

    internal ImportedContentSweepService(
        HealthCheckService? healthCheckService,
        HealthCheckConnectionGate healthCheckConnectionGate,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory,
        string? statePath)
    {
        _healthCheckService = healthCheckService!;
        _healthCheckConnectionGate = healthCheckConnectionGate;
        _dbContextFactory = dbContextFactory;
        StatePath = statePath ?? Path.Join(DavDatabaseContext.ConfigPath, "imported-content-sweep.json");
        _state = LoadState(StatePath);
    }

    internal string StatePath { get; }
    internal TimeSpan PauseBetweenFiles { get; set; } = TimeSpan.FromMilliseconds(250);
    internal Func<DavDatabaseContext>? CreateDbContextOverride { get; set; }

    internal Func<DavItem, DavDatabaseClient, CancellationToken, Task<ArticleContentVerification?>>? VerifyOverride
    { get; set; }

    internal Func<Guid, IReadOnlyCollection<string>, string, CancellationToken, Task<bool>>? QueueRepairOverride
    { get; set; }

    internal Task RunTask
    {
        get
        {
            lock (_lock) return _runTask;
        }
    }

    public ImportedContentSweepState GetState()
    {
        lock (_lock) return _state;
    }

    /// <summary>
    /// Starts (or resumes) the sweep. A different category, or <paramref name="restart"/>,
    /// starts over from the beginning; otherwise a paused sweep continues from its cursor.
    /// Returns false when a sweep is already running.
    /// </summary>
    public bool Start(string? category, int? limit, bool restart)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        if (normalizedCategory is not null && normalizedCategory.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException("Category must be a single folder name under /content.");
        if (limit is <= 0)
            throw new ArgumentException("Limit must be a positive number of files.");

        lock (_lock)
        {
            if (_state.Status == StatusRunning && !_runTask.IsCompleted) return false;
            var startOver = restart
                || _state.Status is StatusCompleted or StatusIdle
                || !string.Equals(_state.Category, normalizedCategory, StringComparison.Ordinal);
            var now = DateTimeOffset.UtcNow;
            _state = startOver
                ? new ImportedContentSweepState
                {
                    Status = StatusRunning,
                    Category = normalizedCategory,
                    Limit = limit,
                    StartedAt = now,
                    UpdatedAt = now,
                    Message = "Starting.",
                }
                : _state with
                {
                    Status = StatusRunning,
                    Limit = limit,
                    CheckedThisRun = 0,
                    FinishedAt = null,
                    UpdatedAt = now,
                    Message = "Resuming.",
                };
            SaveStateLocked();
            StartRunLocked(TimeSpan.Zero);
            return true;
        }
    }

    /// <summary>Stops a running sweep; it can be resumed later from where it stopped.</summary>
    public async Task StopSweepAsync()
    {
        Task task;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _runCts;
            task = _runTask;
        }

        if (cts is not null)
            await CancelQuietlyAsync(cts).ConfigureAwait(false);

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is the expected outcome.
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_lock)
        {
            _stoppingToken = stoppingToken;
            // A sweep that was running when the process stopped resumes after startup settles.
            if (_state.Status == StatusRunning)
                StartRunLocked(ResumeDelay);
        }

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task task;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            // Leave Status as running so the next start resumes automatically.
            cts = _runCts;
            task = _runTask;
        }

        if (cts is not null)
            await CancelQuietlyAsync(cts).ConfigureAwait(false);

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CancelQuietlyAsync(CancellationTokenSource cts)
    {
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A newer run replaced this one.
        }
    }

    private void StartRunLocked(TimeSpan delay)
    {
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        var token = _runCts.Token;
        _runTask = Task.Run(() => RunAsync(token, delay), CancellationToken.None);
    }

    internal async Task RunAsync(CancellationToken ct, TimeSpan delay = default)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            await SweepAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(state => state with
            {
                // Process shutdown keeps "running" so the sweep resumes on the next start.
                Status = _stoppingToken.IsCancellationRequested ? StatusRunning : StatusPaused,
                Message = _stoppingToken.IsCancellationRequested
                    ? "Interrupted by shutdown; resumes after restart."
                    : "Stopped. Start again to resume from where it stopped.",
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            e.TryGetKnownErrorMessage(out var reason);
            Update(state => state with { Status = StatusFailed, Message = $"Failed: {reason}" });
            if (e.TryGetKnownErrorMessage(out _))
            {
                Log.Warning("Imported-content sweep stopped. Reason: {Reason}", reason);
                Log.Debug(e, "Imported-content sweep known failure stack");
            }
            else
            {
                Log.Error(e, "Imported-content sweep failed unexpectedly: {Message}", e.Message);
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var start = GetState();
        Log.Information(
            "Imported-content sweep {Action} for {Scope}{Limit}.",
            start.Cursor is null ? "started" : "resumed",
            start.Category is null ? "all imported files" : $"category {start.Category}",
            start.Limit is { } startLimit ? $" (limit {startLimit} files)" : "");

        using var maintenance = ct.SetContext(MaintenanceDownloadContext.Instance);
        using var admission = ct.SetContext(new HealthCheckAdmissionContext(
            _healthCheckConnectionGate,
            HealthCheckAdmissionPriority.Background));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var state = GetState();
            if (state.Limit is { } limit && state.CheckedThisRun >= limit)
            {
                Update(current => current with
                {
                    Status = StatusPaused,
                    Message = $"Paused after the {limit}-file limit. Start again to continue.",
                });
                LogSummary("paused at its file limit");
                return;
            }

            var batch = await LoadBatchAsync(state.Category, state.Cursor, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                Update(current => current with
                {
                    Status = StatusCompleted,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Message = "Completed.",
                });
                LogSummary("completed");
                return;
            }

            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();
                var current = GetState();
                if (current.Limit is { } itemLimit && current.CheckedThisRun >= itemLimit) break;
                await ProcessItemAsync(item, ct).ConfigureAwait(false);
                if (PauseBetweenFiles > TimeSpan.Zero)
                    await Task.Delay(PauseBetweenFiles, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<DavItem>> LoadBatchAsync(string? category, string? cursor, CancellationToken ct)
    {
        await using var dbContext = CreateContext();
        IQueryable<DavItem> query = dbContext.Items
            .AsNoTracking()
            .Where(x => x.Type == DavItem.ItemType.UsenetFile);
        if (category is not null)
        {
            var prefix = $"{DavItem.ContentFolder.Path}/{category}/";
            query = query.Where(x => x.Path.StartsWith(prefix));
        }

        if (cursor is not null)
            query = query.Where(x => x.Path.CompareTo(cursor) > 0);

        return await query
            .OrderBy(x => x.Path)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task ProcessItemAsync(DavItem item, CancellationToken ct)
    {
        // Only imported media files are verified; extras and items already queued for an
        // urgent or pending repair are skipped (the health check owns those).
        if (!FilenameUtil.IsMediaFile(item.Name)
            || item.NextHealthCheck == DateTimeOffset.UnixEpoch
            || item.HealthRepairPending)
        {
            Update(state => state with
            {
                Cursor = item.Path,
                Skipped = state.Skipped + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return;
        }

        ArticleContentVerification? result;
        await using (var dbContext = CreateContext())
        {
            var dbClient = new DavDatabaseClient(dbContext);
            using var attribution = FetchAttributionContext.Begin(item.Name);
            result = VerifyOverride is { } verify
                ? await verify(item, dbClient, ct).ConfigureAwait(false)
                : await _healthCheckService.VerifyImportedFileContentAsync(
                        item, dbClient, ArticleContentSampleBudget.Import, ArticleConcurrency, ct)
                    .ConfigureAwait(false);
        }

        if (result is null)
        {
            Update(state => state with
            {
                Cursor = item.Path,
                Skipped = state.Skipped + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return;
        }

        if (!result.IsDamaged)
        {
            var unverifiable = result.Sampled > 0 && result.Inconclusive == result.Sampled;
            if (unverifiable)
                Log.Debug("Imported-content sweep could not read any sampled article of {Path}", item.Path);
            else
                Log.Debug("Imported-content sweep verified {Path}: {Detail}", item.Path, result.Describe());
            Update(state => state with
            {
                Cursor = item.Path,
                Checked = state.Checked + 1,
                CheckedThisRun = state.CheckedThisRun + 1,
                Healthy = state.Healthy + (unverifiable ? 0 : 1),
                Unverifiable = state.Unverifiable + (unverifiable ? 1 : 0),
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = $"Checked {item.Name}.",
            });
            MaybeLogProgress();
            return;
        }

        var detail = result.Describe();
        var queued = QueueRepairOverride is { } queueRepair
            ? await queueRepair(item.Id, result.DamagedSegmentIds, detail, ct).ConfigureAwait(false)
            : await _healthCheckService.QueueRepairForDamagedReleaseAsync(
                item.Id, result.DamagedSegmentIds, $"imported-content sweep found {detail}", ct)
                .ConfigureAwait(false);
        if (!queued)
            Log.Warning("Imported-content sweep found {Path} damaged ({Detail}) but it was removed before repair " +
                        "could be queued.", item.Path, detail);
        var finding = new ImportedContentSweepFinding(item.Id, item.Path, detail, DateTimeOffset.UtcNow);
        Update(state => state with
        {
            Cursor = item.Path,
            Checked = state.Checked + 1,
            CheckedThisRun = state.CheckedThisRun + 1,
            Damaged = state.Damaged + 1,
            RepairsQueued = state.RepairsQueued + (queued ? 1 : 0),
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = $"Found {item.Name} damaged; repair queued.",
            RecentFindings = state.RecentFindings.Prepend(finding).Take(MaxRecentFindings).ToList(),
        });
        MaybeLogProgress();
    }

    private void MaybeLogProgress()
    {
        var state = GetState();
        if (state.Checked > 0 && state.Checked % ProgressLogInterval == 0)
            LogSummary("progress");
    }

    private void LogSummary(string phase)
    {
        var state = GetState();
        Log.Information(
            "Imported-content sweep {Phase}: checked {Checked} files ({Healthy} healthy, {Damaged} damaged, " +
            "{Unverifiable} unreadable), skipped {Skipped}, queued {RepairsQueued} repairs.",
            phase, state.Checked, state.Healthy, state.Damaged, state.Unverifiable, state.Skipped,
            state.RepairsQueued);
    }

    private void Update(Func<ImportedContentSweepState, ImportedContentSweepState> mutate)
    {
        lock (_lock)
        {
            _state = mutate(_state) with { UpdatedAt = DateTimeOffset.UtcNow };
            SaveStateLocked();
        }
    }

    private void SaveStateLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Could not save imported-content sweep progress to {Path}. Reason: {Reason}",
                StatePath, e.Message);
        }
    }

    private static ImportedContentSweepState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ImportedContentSweepState();
            return JsonSerializer.Deserialize<ImportedContentSweepState>(File.ReadAllText(path), JsonOptions)
                   ?? new ImportedContentSweepState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("Could not read imported-content sweep progress from {Path}; starting fresh. Reason: {Reason}",
                path, e.Message);
            return new ImportedContentSweepState();
        }
    }

    private DavDatabaseContext CreateContext() =>
        DavDatabaseContexts.Create(CreateDbContextOverride, _dbContextFactory);

    public override void Dispose()
    {
        _runCts?.Dispose();
        base.Dispose();
    }
}
