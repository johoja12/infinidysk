using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Services.Prefetch;

public sealed record PrefetchJob(string Id, Guid ItemId, string Trigger, int Priority, string State,
    long Start, long Length, string? Generation, long CommittedBytes, string? Error, long Updated)
{
    public string? DisplayName { get; init; }
    public string? Source { get; init; }
    public string? Reason { get; init; }
    public long? FileSize { get; init; }
    /// <summary>Unix milliseconds when the job first started running; null for jobs recorded before timing existed.</summary>
    public long? StartedAt { get; init; }
    /// <summary>Unix milliseconds when the job reached a terminal state.</summary>
    public long? FinishedAt { get; init; }
    /// <summary>Milliseconds the job spent running, excluding queued, deferred, and paused time.</summary>
    public long? ActiveMs { get; init; }
    /// <summary>Bytes this job fetched and committed to the cache; excludes bytes that were already cached.</summary>
    public long? WarmedBytes { get; init; }
    /// <summary>
    /// Bytes of a range job's own range, widened to whole integrity blocks. Null for whole-file jobs
    /// and when the range cannot be resolved against the current file.
    /// </summary>
    public long? RangeBytes { get; init; }
    /// <summary>
    /// Bytes of <see cref="RangeBytes"/> verified in the Native Cache catalogue for the item's current
    /// revision. Null for whole-file jobs and when the revision or catalogue is unavailable.
    /// </summary>
    public long? RangeCachedBytes { get; init; }
    /// <summary>
    /// Stable reason code for the latest failure or deferral, for example <c>source-damaged</c>,
    /// <c>cache-storage</c>, <c>source-changed</c> or <c>budget</c>. Null while healthy and for older rows.
    /// </summary>
    public string? FailureCode { get; init; }
    /// <summary>Follow-up taken for a failure, for example <c>repair-queued</c>; null when none.</summary>
    public string? Remedy { get; init; }
    /// <summary>Live repair outcome; never changes this attempt's failure or cache accounting.</summary>
    public PrefetchRepairOutcome? RepairOutcome { get; init; }
    /// <summary>Running jobs only: bytes per second fetched over the last few seconds; null until measurable.</summary>
    public double? RecentBytesPerSecond { get; init; }
    /// <summary>Running jobs only: Unix milliseconds of the latest progress report (fetch or verification).</summary>
    public long? LastProgressAt { get; init; }
    /// <summary>Running jobs only: true when no progress was reported for a minute.</summary>
    public bool? Stalled { get; init; }
    /// <summary>
    /// Every source that requested this job, resolved to a display label and category, most specific
    /// first and capped at eight; <see cref="SourceCount"/> is the full number.
    /// </summary>
    public IReadOnlyList<PrefetchJobSource>? Sources { get; init; }
    public int? SourceCount { get; init; }
    /// <summary>
    /// Backfill jobs only: why playback served these bytes without caching them, as a
    /// <see cref="NzbWebDAV.Streams.BackfillMissReasons"/> code. Null for other jobs and older rows.
    /// </summary>
    public string? MissReason { get; init; }
    /// <summary>Backfill jobs only: the Plex user who was playing, when Plex reported the session.</summary>
    public string? ViewerUser { get; init; }
    /// <summary>Backfill jobs only: the Plex player (device) that was playing.</summary>
    public string? ViewerPlayer { get; init; }
    /// <summary>Backfill jobs only: media runtime in milliseconds from Plex, for placing the range in time.</summary>
    public long? MediaDurationMs { get; init; }
    /// <summary>True for jobs that warm part of a file (backfill, head/tail minimum, resume range).</summary>
    public bool IsRangeJob => Start != 0 || Length != 0;
}

public sealed record PrefetchEnqueueResult(PrefetchJob Job, bool Created);

/// <summary>A job owner as shown to people: a concrete label (e.g. a Plex hub title) and a stable category.</summary>
public sealed record PrefetchJobSource(string Label, string Category);

public sealed class PrefetchJobStore : IDisposable
{
    private readonly SqliteConnection _database;
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly Func<PrefetchSettings>? _settings;
    private int Capacity => Math.Min(_capacity, _settings?.Invoke().QueueCapacity ?? _capacity);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Non-owning alias: Atomic owns the transaction with a using declaration and clears this reference in finally.")]
    private SqliteTransaction? _transaction;
    private int _wireBudgetBlocked;
    private readonly CancellationTokenSource _wireBudgetFailure = new();
    public bool WireBudgetBlocked => Volatile.Read(ref _wireBudgetBlocked) != 0;
    /// <summary>Live per-job warming rate, fed by <see cref="Progress"/>.</summary>
    public WarmingRateWindow Rates { get; } = new();
    public CancellationToken WireBudgetFailure => _wireBudgetFailure.Token;
    public void BlockWireBudget()
    {
        if (Interlocked.Exchange(ref _wireBudgetBlocked, 1) == 0)
            try { _wireBudgetFailure.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
    public PrefetchJobStore(string path, int capacity = 256, Func<PrefetchSettings>? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        NativeFileSystem.RequireLocalMetadata(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _capacity = capacity;
        _settings = settings;
        _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _database.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA cache_size=-2048;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS Jobs(
                Id TEXT PRIMARY KEY,ItemId TEXT NOT NULL,Trigger TEXT NOT NULL,Priority INTEGER NOT NULL,
                State TEXT NOT NULL,Start INTEGER NOT NULL,Length INTEGER NOT NULL,Generation TEXT NULL,
                CommittedBytes INTEGER NOT NULL DEFAULT 0,Error TEXT NULL,Updated INTEGER NOT NULL,Created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS JobReady ON Jobs(State,Priority DESC,Created);
            CREATE INDEX IF NOT EXISTS JobItem ON Jobs(ItemId,Start,Length,State);
            CREATE TABLE IF NOT EXISTS State(Key TEXT PRIMARY KEY,Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Deferred(Id TEXT PRIMARY KEY,Until INTEGER NOT NULL);
            CREATE TRIGGER IF NOT EXISTS JobDeleted AFTER DELETE ON Jobs BEGIN DELETE FROM Deferred WHERE Id=OLD.Id; END;
            CREATE TABLE IF NOT EXISTS DailyBudget(Day TEXT PRIMARY KEY,Bytes INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Owners(Id TEXT NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,Owner TEXT NOT NULL,PRIMARY KEY(Id,Owner));
            CREATE TABLE IF NOT EXISTS Attempts(Id TEXT PRIMARY KEY REFERENCES Jobs(Id) ON DELETE CASCADE,Count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Verified(ItemId TEXT NOT NULL,Generation TEXT NOT NULL,Start INTEGER NOT NULL,Length INTEGER NOT NULL,
                At INTEGER NOT NULL,PRIMARY KEY(ItemId,Generation,Start,Length));
            CREATE TABLE IF NOT EXISTS Damaged(ItemId TEXT PRIMARY KEY,Generation TEXT NULL,Until INTEGER NOT NULL);
            INSERT OR IGNORE INTO State VALUES('paused','false');
            """);
        AddMissingJobColumns();
        AddMissingColumns("Verified", ("Fingerprint", "TEXT"));
        Execute("""
            UPDATE Jobs SET ActiveMs=COALESCE(ActiveMs,0)+MAX(0,Updated-RunStarted),RunStarted=NULL WHERE RunStarted IS NOT NULL;
            DELETE FROM Jobs WHERE State IN ('queued','running','paused') AND Id NOT IN (SELECT Id FROM Owners WHERE Owner='manual');
            UPDATE Jobs SET State='paused',Error='Restored after restart; resume to recheck verified coverage.' WHERE State IN ('running','queued');
            DELETE FROM Deferred WHERE Id NOT IN (SELECT Id FROM Jobs);
            """);
    }

    // Adds the running time to ActiveMs and stops the run clock. Requires a $now parameter.
    private const string CloseRunClock = "ActiveMs=CASE WHEN RunStarted IS NULL THEN ActiveMs ELSE COALESCE(ActiveMs,0)+MAX(0,$now-RunStarted) END,RunStarted=NULL";

    /// <summary>
    /// Additive schema upgrade for databases created before job timing existed. Every column is
    /// nullable, so older rows read as "not recorded".
    /// </summary>
    private void AddMissingColumns(string table, params (string Column, string Type)[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command($"PRAGMA table_info({table})"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) existing.Add(reader.GetString(1));
        foreach (var (column, type) in columns)
            if (!existing.Contains(column)) Execute($"ALTER TABLE {table} ADD COLUMN {column} {type} NULL");
    }

    private void AddMissingJobColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command("PRAGMA table_info(Jobs)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) existing.Add(reader.GetString(1));
        foreach (var (column, type) in new[] { ("StartedAt", "INTEGER"), ("FinishedAt", "INTEGER"), ("ActiveMs", "INTEGER"),
            ("RunStarted", "INTEGER"), ("WarmedBytes", "INTEGER"), ("FailureCode", "TEXT"), ("Remedy", "TEXT"),
            ("MissReason", "TEXT"), ("ViewerUser", "TEXT"), ("ViewerPlayer", "TEXT"), ("MediaDurationMs", "INTEGER") })
            if (!existing.Contains(column)) Execute($"ALTER TABLE Jobs ADD COLUMN {column} {type} NULL");
    }

    public PrefetchJob Enqueue(Guid itemId, string trigger, int priority, long start = 0, long length = 0)
        => EnqueueWithOutcome(itemId, trigger, priority, start, length).Job;

    public PrefetchEnqueueResult EnqueueWithOutcome(Guid itemId, string trigger, int priority, long start = 0, long length = 0)
        => EnqueueCore(itemId, trigger, priority, start, length, mergeGap: 0, reviveGeneration: null, reviveSince: null);

    /// <summary>
    /// Schedules a backfill range. Nearby ranges (within <paramref name="mergeGap"/>) join a queued job of
    /// the same item, or reopen a backfill-only job of the same cache generation that completed since
    /// <paramref name="reviveSince"/>, so one playback session produces a handful of rows instead of
    /// one per skipped block. Warming skips blocks that are already cached, so the union is safe.
    /// </summary>
    public PrefetchEnqueueResult EnqueueBackfill(Guid itemId, string owner, int priority, long start, long length,
        string? generation, long mergeGap, DateTimeOffset reviveSince)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mergeGap);
        if (length <= 0) throw new ArgumentException("Backfill needs a bounded range.");
        return EnqueueCore(itemId, owner, priority, start, length, mergeGap, generation, reviveSince);
    }

    private PrefetchEnqueueResult EnqueueCore(Guid itemId, string trigger, int priority, long start, long length,
        long mergeGap, string? reviveGeneration, DateTimeOffset? reviveSince)
    {
        if (itemId == Guid.Empty || trigger is not { Length: > 0 and <= 128 } || priority is < -100 or > 100
            || start < 0 || length < 0 || start > long.MaxValue - length)
            throw new ArgumentException("Invalid prefetch item, trigger, priority, or range.");
        lock (_gate)
        return Atomic(() =>
        {
            // Inactive exact requests retain their retry/pause semantics. Running
            // work is never widened; larger requests become queued successors.
            var existing = ReadOne("SELECT * FROM Jobs WHERE ItemId=$item AND State IN ('running','paused','failed') AND ((Start=$start AND Length=$length) OR (State='running' AND Start=0 AND Length=0)) ORDER BY Created,Id LIMIT 1",
                ("$item", itemId.ToString("N")), ("$start", start), ("$length", length));
            if (existing is not null)
            {
                Execute("UPDATE Jobs SET Priority=MAX(Priority,$priority) WHERE Id=$id", ("$priority", priority), ("$id", existing.Id));
                AddOwner(existing.Id, trigger);
                return new PrefetchEnqueueResult(existing with { Priority = Math.Max(existing.Priority, priority) }, false);
            }
            var merged = MergeQueued(itemId, trigger, priority, start, length, mergeGap);
            if (merged is not null) return new PrefetchEnqueueResult(merged, false);
            using var count = Command("SELECT COUNT(*) FROM Jobs WHERE State IN ('queued','running','paused')");
            if ((long)count.ExecuteScalar()! >= Capacity) throw new ArgumentException("Prefetch queue is full. Cancel or complete existing work first.");
            if (reviveGeneration is not null && reviveSince is { } since
                && ReviveCompleted(itemId, trigger, priority, start, length, mergeGap, reviveGeneration, since) is { } revived)
                return new PrefetchEnqueueResult(revived, false);
            // Retain only bounded operation history; never grow a media-library-sized memory/index queue.
            Execute("DELETE FROM Jobs WHERE Id IN (SELECT Id FROM Jobs WHERE State IN ('completed','failed','cancelled') ORDER BY Updated DESC LIMIT -1 OFFSET 255)");
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var created = NextCreated(now);
            Execute("INSERT INTO Jobs(Id,ItemId,Trigger,Priority,State,Start,Length,Updated,Created) VALUES($id,$item,$trigger,$priority,'queued',$start,$length,$now,$created)",
                ("$id", id), ("$item", itemId.ToString("N")), ("$trigger", trigger), ("$priority", priority), ("$start", start), ("$length", length), ("$now", now), ("$created", created));
            AddOwner(id, trigger);
            return new PrefetchEnqueueResult(new PrefetchJob(id, itemId, trigger, priority, "queued", start, length, null, 0, null, now), true);
        });
    }

    /// <summary>
    /// Labels a backfill job with why playback missed the cache and who was playing. The latest
    /// playback wins for merged or reopened jobs; a known viewer is kept when the new one is unknown.
    /// </summary>
    public void RecordBackfillContext(string id, string reason, PlaybackViewer? viewer)
    {
        lock (_gate)
            Execute("""
                UPDATE Jobs SET MissReason=$reason,ViewerUser=COALESCE($user,ViewerUser),ViewerPlayer=COALESCE($player,ViewerPlayer),
                MediaDurationMs=COALESCE($duration,MediaDurationMs) WHERE Id=$id
                """, ("$id", id), ("$reason", reason), ("$user", Bounded(viewer?.User) ?? (object)DBNull.Value),
                ("$player", Bounded(viewer?.Player) ?? (object)DBNull.Value),
                ("$duration", viewer is { DurationMs: > 0 } ? viewer.DurationMs : DBNull.Value));
    }

    private static string? Bounded(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= 128 ? value : value[..128];

    private sealed record QueuedIntent(PrefetchJob Job, long Created, long Attempts, long? DeferredUntil);

    /// <summary>
    /// Reopens the newest completed job of this item and generation that only <paramref name="owner"/>
    /// requested and whose range lies within <paramref name="gap"/> of the new one, widened to their union.
    /// </summary>
    private PrefetchJob? ReviveCompleted(Guid itemId, string owner, int priority, long start, long length, long gap,
        string generation, DateTimeOffset since)
    {
        var end = start + length;
        var job = ReadOne("""
            SELECT * FROM Jobs j WHERE j.ItemId=$item AND j.State='completed' AND j.Generation=$generation AND j.Updated>=$since
            AND j.Length>0 AND j.Start<=$end+$gap AND j.Start+j.Length+$gap>=$start
            AND EXISTS (SELECT 1 FROM Owners o WHERE o.Id=j.Id AND o.Owner=$owner)
            AND NOT EXISTS (SELECT 1 FROM Owners o WHERE o.Id=j.Id AND o.Owner<>$owner)
            ORDER BY j.Updated DESC,j.Id LIMIT 1
            """, ("$item", itemId.ToString("N")), ("$generation", generation), ("$since", since.ToUnixTimeMilliseconds()),
            ("$start", start), ("$end", end), ("$gap", gap), ("$owner", owner));
        if (job is null) return null;
        var unionStart = Math.Min(start, job.Start);
        var unionEnd = Math.Max(end, job.Start + job.Length);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Execute("UPDATE Jobs SET State='queued',Start=$start,Length=$length,Priority=MAX(Priority,$priority),Error=NULL,FinishedAt=NULL,Updated=$now,Created=$created WHERE Id=$id",
            ("$start", unionStart), ("$length", unionEnd - unionStart), ("$priority", priority), ("$now", now),
            ("$created", NextCreated(now)), ("$id", job.Id));
        Execute("DELETE FROM Attempts WHERE Id=$id", ("$id", job.Id));
        Execute("DELETE FROM Deferred WHERE Id=$id", ("$id", job.Id));
        return ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", job.Id))!;
    }

    private PrefetchJob? MergeQueued(Guid itemId, string owner, int priority, long start, long length, long gap = 0)
    {
        var candidates = new List<QueuedIntent>();
        using (var query = Command("""
            SELECT j.*,COALESCE(a.Count,0) AS AttemptCount,d.Until FROM Jobs j
            LEFT JOIN Attempts a ON a.Id=j.Id LEFT JOIN Deferred d ON d.Id=j.Id
            WHERE j.ItemId=$item AND j.State='queued' ORDER BY j.Created,j.Id LIMIT 257
            """, ("$item", itemId.ToString("N"))))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) candidates.Add(new(Read(reader), reader.GetInt64(reader.GetOrdinal("Created")),
                reader.GetInt64(reader.GetOrdinal("AttemptCount")), reader.IsDBNull(reader.GetOrdinal("Until")) ? null : reader.GetInt64(reader.GetOrdinal("Until"))));
        if (candidates.Count > 256) throw new ArgumentException("Prefetch queue exceeds its bounded capacity. Clear excess intents first.");

        // Null is an open-ended interval, including a nonzero-start tail request.
        long? end = length == 0 ? null : start + length;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in candidates)
            {
                var job = candidate.Job;
                long? candidateEnd = job.Length == 0 ? null : checked(job.Start + job.Length);
                if (selected.Contains(job.Id) || (end.HasValue && job.Start > end.Value + gap)
                    || (candidateEnd.HasValue && start > candidateEnd.Value + gap)) continue;
                selected.Add(job.Id);
                start = Math.Min(start, job.Start);
                end = !end.HasValue || !candidateEnd.HasValue ? null : Math.Max(end.Value, candidateEnd.Value);
                changed = true;
            }
        } while (changed);
        if (selected.Count == 0) return null;
        var merged = candidates.Where(candidate => selected.Contains(candidate.Job.Id)).ToArray();
        var survivor = merged[0]; // Query order preserves the oldest intent and stable identity.
        var owners = new HashSet<string>(StringComparer.Ordinal) { owner };
        foreach (var candidate in merged)
        {
            using var query = Command("SELECT Owner FROM Owners WHERE Id=$id LIMIT 130", ("$id", candidate.Job.Id));
            using var reader = query.ExecuteReader();
            while (reader.Read()) owners.Add(reader.GetString(0));
            if (owners.Count - (owners.Contains("manual") ? 1 : 0) > 128)
                throw new ArgumentException("Merging these ranges would exceed the prefetch source-owner limit.");
        }

        var id = survivor.Job.Id;
        foreach (var source in owners) Execute("INSERT OR IGNORE INTO Owners(Id,Owner) VALUES($id,$owner)", ("$id", id), ("$owner", source));
        var attempts = merged.Max(candidate => candidate.Attempts);
        var until = merged.Max(candidate => candidate.DeferredUntil);
        Execute("UPDATE Jobs SET Start=$start,Length=$length,Priority=$priority,Created=$created WHERE Id=$id",
            ("$start", start), ("$length", end.HasValue ? end.Value - start : 0),
            ("$priority", Math.Max(priority, merged.Max(candidate => candidate.Job.Priority))), ("$created", survivor.Created), ("$id", id));
        Execute("INSERT INTO Attempts(Id,Count) VALUES($id,$count) ON CONFLICT(Id) DO UPDATE SET Count=excluded.Count", ("$id", id), ("$count", attempts));
        if (until.HasValue)
            Execute("INSERT INTO Deferred(Id,Until) VALUES($id,$until) ON CONFLICT(Id) DO UPDATE SET Until=excluded.Until", ("$id", id), ("$until", until.Value));
        foreach (var candidate in merged.Skip(1)) Execute("DELETE FROM Jobs WHERE Id=$id", ("$id", candidate.Job.Id));
        return ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id))!;
    }

    public bool Paused
    {
        get { lock (_gate) { using var command = Command("SELECT Value FROM State WHERE Key='paused'"); return command.ExecuteScalar() as string == "true"; } }
    }

    /// <returns>True when the deferral used the job's last retry and it is now failed.</returns>
    public bool Defer(string id, string reason, TimeSpan delay, bool consumeAttempt = true, string? failureCode = null)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(delay));
        Rates.Stop(id);
        lock (_gate)
        return Atomic(() =>
        {
            if (!IsRunning(id)) return false;
            if (consumeAttempt) Execute("INSERT INTO Attempts(Id,Count) SELECT Id,1 FROM Jobs WHERE Id=$id AND State='running' ON CONFLICT(Id) DO UPDATE SET Count=Count+1", ("$id", id));
            Execute("UPDATE Jobs SET State='queued',Error=$reason,FailureCode=$code,Remedy=NULL WHERE Id=$id AND State='running'", ("$id", id),
                ("$reason", reason[..Math.Min(512, reason.Length)]), ("$code", (object?)failureCode ?? DBNull.Value));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Execute($"UPDATE Jobs SET {CloseRunClock} WHERE Id=$id AND RunStarted IS NOT NULL", ("$id", id), ("$now", now));
            Execute("UPDATE Jobs SET State='failed',FinishedAt=$now WHERE Id=$id AND State='queued' AND Id IN (SELECT Id FROM Attempts WHERE Count>$retries)",
                ("$id", id), ("$retries", _settings?.Invoke().MaxRetries ?? 3), ("$now", now));
            Execute("INSERT INTO Deferred(Id,Until) VALUES($id,$until) ON CONFLICT(Id) DO UPDATE SET Until=excluded.Until",
                ("$id", id), ("$until", DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds()));
            return ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id))?.State == "failed";
        });
    }

    /// <summary>Failed attempts already counted against this job's retry budget.</summary>
    public long Attempts(string id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT Count FROM Attempts WHERE Id=$id", ("$id", id));
            return command.ExecuteScalar() as long? ?? 0;
        }
    }

    /// <summary>
    /// Remembers that warming proved this item's release damaged, so routine policy refreshes do not
    /// re-enqueue it until <paramref name="until"/> or until its cache revision changes (repair).
    /// </summary>
    public void MarkDamaged(Guid itemId, string? generation, DateTimeOffset until)
    {
        if (generation?.Length > 512) throw new ArgumentException("Invalid cache generation.");
        lock (_gate)
        Atomic(() =>
        {
            Execute("INSERT INTO Damaged(ItemId,Generation,Until) VALUES($item,$generation,$until) ON CONFLICT(ItemId) DO UPDATE SET Generation=excluded.Generation,Until=excluded.Until",
                ("$item", itemId.ToString("N")), ("$generation", (object?)generation ?? DBNull.Value), ("$until", until.ToUnixTimeMilliseconds()));
            Execute("DELETE FROM Damaged WHERE Until<$now", ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Execute("DELETE FROM Damaged WHERE rowid IN (SELECT rowid FROM Damaged ORDER BY Until DESC LIMIT -1 OFFSET 4096)");
        });
    }

    /// <summary>
    /// True while a damaged-release verdict suppresses routine warming of this item. A different
    /// <paramref name="currentGeneration"/> means the source was repaired or replaced, which clears it.
    /// </summary>
    public bool IsDamaged(Guid itemId, string? currentGeneration, DateTimeOffset now)
    {
        lock (_gate)
        {
            string? generation;
            long until;
            using (var command = Command("SELECT Generation,Until FROM Damaged WHERE ItemId=$item", ("$item", itemId.ToString("N"))))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return false;
                generation = reader.IsDBNull(0) ? null : reader.GetString(0);
                until = reader.GetInt64(1);
            }
            if (until > now.ToUnixTimeMilliseconds()
                && (generation is null || currentGeneration is null || generation == currentGeneration)) return true;
            Execute("DELETE FROM Damaged WHERE ItemId=$item", ("$item", itemId.ToString("N")));
            return false;
        }
    }

    public void PruneOwners(Func<string, bool> keep)
    {
        lock (_gate)
        Atomic(() =>
        {
            var removed = new List<(string Id, string Owner)>();
            using (var command = Command("SELECT Id,Owner FROM Owners"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) if (!keep(reader.GetString(1))) removed.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var owner in removed) Execute("DELETE FROM Owners WHERE Id=$id AND Owner=$owner", ("$id", owner.Id), ("$owner", owner.Owner));
            Execute($"UPDATE Jobs SET State='cancelled',Error='All sources disabled.',FinishedAt=$now,{CloseRunClock} WHERE State IN ('queued','running','paused') AND Id NOT IN (SELECT Id FROM Owners)",
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        });
    }

    private void AddOwner(string id, string owner)
    {
        using var existing = Command("SELECT 1 FROM Owners WHERE Id=$id AND Owner=$owner", ("$id", id), ("$owner", owner));
        if (existing.ExecuteScalar() is not null) return;
        using var count = Command("SELECT COUNT(*) FROM Owners WHERE Id=$id AND Owner<>'manual'", ("$id", id));
        if ((long)count.ExecuteScalar()! >= 128 && owner != "manual")
            throw new ArgumentException("Prefetch source-owner limit reached.");
        Execute("INSERT OR IGNORE INTO Owners(Id,Owner) VALUES($id,$owner)", ("$id", id), ("$owner", owner));
    }

    /// <summary>Owners of the given jobs, in a stable order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> OwnersOf(IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var command = Command("SELECT Id,Owner FROM Owners ORDER BY Id,Owner");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!wanted.Contains(id)) continue;
                if (!result.TryGetValue(id, out var owners)) result[id] = owners = [];
                owners.Add(reader.GetString(1));
            }
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
    }

    public bool HasOwner(string id, string owner)
    {
        lock (_gate)
        {
            using var command = Command("SELECT 1 FROM Owners WHERE Id=$id AND Owner=$owner", ("$id", id), ("$owner", owner));
            return command.ExecuteScalar() is not null;
        }
    }

    public bool IsRunning(string id)
    {
        lock (_gate) return ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id))?.State == "running";
    }

    public bool TrySpendDailyBudget(long bytes, long budget)
    {
        if (bytes < 0 || budget < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Execute("DELETE FROM DailyBudget WHERE Day<>$day", ("$day", day));
            using var lookup = Command("SELECT Bytes FROM DailyBudget WHERE Day=$day", ("$day", day));
            var spent = lookup.ExecuteScalar() as long? ?? 0;
            if (budget != 0 && bytes > budget - spent) return false;
            Execute("INSERT INTO DailyBudget(Day,Bytes) VALUES($day,$bytes) ON CONFLICT(Day) DO UPDATE SET Bytes=Bytes+excluded.Bytes", ("$day", day), ("$bytes", bytes));
            return true;
        }
    }

    public long ReserveDailyCredit(long requested, long budget, string day)
    {
        if (WireBudgetBlocked) throw new IOException("Warming budget accounting failed; repair local metadata storage and restart.");
        if (requested < 0 || budget < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        lock (_gate)
        return Atomic(() =>
        {
            Execute("DELETE FROM DailyBudget WHERE Day<$day", ("$day", day));
            using var lookup = Command("SELECT Bytes FROM DailyBudget WHERE Day=$day", ("$day", day));
            var spent = lookup.ExecuteScalar() as long? ?? 0;
            var grant = budget == 0 ? requested : Math.Min(requested, Math.Max(0, budget - spent));
            Execute("INSERT INTO DailyBudget(Day,Bytes) VALUES($day,$bytes) ON CONFLICT(Day) DO UPDATE SET Bytes=Bytes+excluded.Bytes", ("$day", day), ("$bytes", grant));
            return grant;
        });
    }

    public void ReturnDailyCredit(long bytes, string day)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate) Execute("UPDATE DailyBudget SET Bytes=MAX(0,Bytes-$bytes) WHERE Day=$day", ("$day", day), ("$bytes", bytes));
    }

    public long GetDailyBudgetUsed()
    {
        lock (_gate)
        {
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using var lookup = Command("SELECT Bytes FROM DailyBudget WHERE Day=$day", ("$day", day));
            return lookup.ExecuteScalar() as long? ?? 0;
        }
    }

    /// <param name="maxRangeLength">When set, claims only range jobs of at most this many bytes.</param>
    public PrefetchJob? ClaimNext(long? maxRangeLength = null)
    {
        lock (_gate)
        {
            if (Paused) return null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Execute("UPDATE Jobs SET State='failed',Error='Intent expired; retry explicitly.',FinishedAt=$now WHERE State='queued' AND Created<$cutoff",
                ("$cutoff", DateTimeOffset.UtcNow.AddHours(-(_settings?.Invoke().IntentTtlHours ?? 24)).ToUnixTimeMilliseconds()), ("$now", now));
            var job = ReadOne("SELECT * FROM Jobs WHERE State='queued' AND ($max IS NULL OR Length BETWEEN 1 AND $max) AND ItemId NOT IN (SELECT ItemId FROM Jobs WHERE State='running') AND Id NOT IN (SELECT Id FROM Deferred WHERE Until>$now) ORDER BY Priority DESC,Created,Id LIMIT 1",
                ("$now", now), ("$max", (object?)maxRangeLength ?? DBNull.Value));
            if (job is null) return null;
            Execute("UPDATE Jobs SET State='running',Error=NULL,FailureCode=NULL,Remedy=NULL,StartedAt=COALESCE(StartedAt,$now),RunStarted=$now,ActiveMs=COALESCE(ActiveMs,0),WarmedBytes=COALESCE(WarmedBytes,0) WHERE Id=$id",
                ("$id", job.Id), ("$now", now));
            Rates.Start(job.Id, DateTimeOffset.FromUnixTimeMilliseconds(now));
            return job with { State = "running", Error = null, FailureCode = null, Remedy = null, StartedAt = job.StartedAt ?? now, ActiveMs = job.ActiveMs ?? 0, WarmedBytes = job.WarmedBytes ?? 0 };
        }
    }

    /// <summary>
    /// Whether <see cref="ClaimNext"/> could hand out a job outranking <paramref name="priority"/> right now.
    /// Range jobs of at most <paramref name="expressBytes"/> are left out: they run in their own slot.
    /// </summary>
    public bool HasClaimableAbove(int priority, long expressBytes)
    {
        lock (_gate)
        {
            if (Paused) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var command = Command("SELECT 1 FROM Jobs WHERE State='queued' AND Priority>$priority AND Created>=$cutoff AND NOT (Length BETWEEN 1 AND $express) AND ItemId NOT IN (SELECT ItemId FROM Jobs WHERE State='running') AND Id NOT IN (SELECT Id FROM Deferred WHERE Until>$now) LIMIT 1",
                ("$priority", priority), ("$express", expressBytes), ("$now", now),
                ("$cutoff", DateTimeOffset.UtcNow.AddHours(-(_settings?.Invoke().IntentTtlHours ?? 24)).ToUnixTimeMilliseconds()));
            return command.ExecuteScalar() is not null;
        }
    }

    /// <param name="committedBytes">Whole-file cache coverage after this step, including bytes that were already cached.</param>
    /// <param name="warmedBytes">Bytes this job fetched and committed since its previous progress report.</param>
    public void Progress(string id, string generation, long committedBytes, long warmedBytes = 0)
    {
        if (committedBytes < 0 || warmedBytes < 0 || generation.Length > 512) throw new ArgumentException("Invalid committed progress.");
        Rates.Record(id, warmedBytes, DateTimeOffset.UtcNow);
        lock (_gate) Execute("UPDATE Jobs SET Generation=$generation,CommittedBytes=$bytes,WarmedBytes=COALESCE(WarmedBytes,0)+$warmed,Updated=$now WHERE Id=$id AND State='running'",
            ("$generation", generation), ("$bytes", committedBytes), ("$warmed", warmedBytes), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id));
    }

    /// <summary>
    /// Records that a completed warm verified every cached block of this range for one cache
    /// generation, so routine policy refreshes can skip re-reading it within the intent window,
    /// or for as long as the catalogue <paramref name="fingerprint"/> stays the same.
    /// </summary>
    public void RecordVerified(Guid itemId, string generation, long start, long length, string? fingerprint = null)
    {
        if (start < 0 || length < 0 || generation is not { Length: > 0 and <= 512 }) throw new ArgumentException("Invalid verified range.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        Atomic(() =>
        {
            Execute("INSERT INTO Verified(ItemId,Generation,Start,Length,At,Fingerprint) VALUES($item,$generation,$start,$length,$now,$fingerprint) ON CONFLICT(ItemId,Generation,Start,Length) DO UPDATE SET At=excluded.At,Fingerprint=excluded.Fingerprint",
                ("$item", itemId.ToString("N")), ("$generation", generation), ("$start", start), ("$length", length), ("$now", now),
                ("$fingerprint", (object?)fingerprint ?? DBNull.Value));
            // Bounded bookkeeping: the intent window is at most 168 hours, so older rows are never consulted.
            Execute("DELETE FROM Verified WHERE At<$cutoff", ("$cutoff", DateTimeOffset.UtcNow.AddHours(-168).ToUnixTimeMilliseconds()));
            Execute("DELETE FROM Verified WHERE rowid IN (SELECT rowid FROM Verified ORDER BY At DESC LIMIT -1 OFFSET 4096)");
        });
    }

    /// <summary>
    /// True when a verification of this generation recorded since <paramref name="since"/> contains the
    /// range. A length of 0 means "to the end of the file" for both the request and the record.
    /// </summary>
    public bool WasVerifiedSince(Guid itemId, string generation, long start, long length, DateTimeOffset since)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT 1 FROM Verified WHERE ItemId=$item AND Generation=$generation AND At>=$since AND Start<=$start " +
                "AND (Length=0 OR ($length<>0 AND $start+$length<=Start+Length)) LIMIT 1",
                ("$item", itemId.ToString("N")), ("$generation", generation), ("$since", since.ToUnixTimeMilliseconds()),
                ("$start", start), ("$length", length));
            return command.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// True when a retained verification (at most 168 hours old) of this generation contains the range
    /// and recorded the same catalogue fingerprint: the cached blocks have not changed since.
    /// </summary>
    public bool WasVerifiedUnchanged(Guid itemId, string generation, long start, long length, string fingerprint)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT 1 FROM Verified WHERE ItemId=$item AND Generation=$generation AND Fingerprint=$fingerprint AND Start<=$start " +
                "AND (Length=0 OR ($length<>0 AND $start+$length<=Start+Length)) LIMIT 1",
                ("$item", itemId.ToString("N")), ("$generation", generation), ("$fingerprint", fingerprint),
                ("$start", start), ("$length", length));
            return command.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// Spends <paramref name="bytes"/> of today's (UTC) routine cache-verification reads; false once the
    /// day's <paramref name="dailyLimit"/> would be exceeded, leaving the total unchanged.
    /// </summary>
    public bool TryConsumeVerificationBytes(long bytes, long dailyLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var key = "verify-bytes:" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        lock (_gate)
            return Atomic(() =>
            {
                using var read = Command("SELECT Value FROM State WHERE Key=$key", ("$key", key));
                var used = long.TryParse(read.ExecuteScalar() as string, out var value) ? value : 0;
                if (used + bytes > dailyLimit) return false;
                Execute("INSERT INTO State(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value",
                    ("$key", key), ("$value", (used + bytes).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Execute("DELETE FROM State WHERE Key LIKE 'verify-bytes:%' AND Key<>$key", ("$key", key));
                return true;
            });
    }

    public void Change(string id, string operation, int? priority = null)
    {
        lock (_gate)
        Atomic(() =>
        {
            var job = ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id)) ?? throw new ArgumentException("Unknown prefetch job.");
            if (operation == "prioritize")
            {
                if (priority is null or < -100 or > 100) throw new ArgumentException("Priority must be between -100 and 100.");
                Execute("UPDATE Jobs SET Priority=$priority WHERE Id=$id", ("$priority", priority.Value), ("$id", id));
                return;
            }
            var state = operation switch
            {
                "pause" when job.State is "queued" or "running" => "paused",
                "resume" when job.State == "paused" => "queued",
                "cancel" when job.State is "queued" or "running" or "paused" => "cancelled",
                "retry" when job.State is "failed" or "cancelled" => "queued",
                _ => throw new ArgumentException("This operation is not valid for the job's current state.")
            };
            if (state == "queued" && job.State is "failed" or "cancelled")
            {
                using var count = Command("SELECT COUNT(*) FROM Jobs WHERE State IN ('queued','running','paused')");
                if ((long)count.ExecuteScalar()! >= Capacity) throw new ArgumentException("Prefetch queue is full.");
                var duplicate = ReadOne("SELECT * FROM Jobs WHERE ItemId=$item AND Id<>$id AND Start=$start AND Length=$length AND State IN ('queued','running','paused') LIMIT 1",
                    ("$item", job.ItemId.ToString("N")), ("$id", id), ("$start", job.Start), ("$length", job.Length));
                if (duplicate is not null) throw new ArgumentException("This file range is already queued.");
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Execute($"UPDATE Jobs SET State=$state,Error=NULL,FailureCode=NULL,Remedy=NULL,Updated=$now,{CloseRunClock},FinishedAt=CASE WHEN $state='cancelled' THEN $now ELSE NULL END WHERE Id=$id",
                ("$state", state), ("$now", now), ("$id", id));
            // A retry is a fresh attempt, so its speed and duration are measured from scratch.
            if (operation == "retry")
                Execute("UPDATE Jobs SET StartedAt=NULL,ActiveMs=NULL,WarmedBytes=NULL WHERE Id=$id", ("$id", id));
            if (operation is "retry" or "resume")
            {
                Execute("DELETE FROM Deferred WHERE Id=$id", ("$id", id));
                if (operation == "retry") Execute("DELETE FROM Attempts WHERE Id=$id", ("$id", id));
                Execute("UPDATE Jobs SET Created=$created WHERE Id=$id", ("$id", id),
                    ("$created", NextCreated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            }
        });
    }

    public void Finish(string id, bool success, string? error, string? failureCode = null, string? remedy = null)
    {
        Rates.Stop(id);
        lock (_gate) Execute($"UPDATE Jobs SET State=$state,Error=$error,FailureCode=$code,Remedy=$remedy,Updated=$now,FinishedAt=$now,{CloseRunClock} WHERE Id=$id AND State='running'",
            ("$state", success ? "completed" : "failed"), ("$error", error is null ? DBNull.Value : error[..Math.Min(error.Length, 512)]),
            ("$code", (object?)failureCode ?? DBNull.Value), ("$remedy", (object?)remedy ?? DBNull.Value),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id));
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) Execute("UPDATE State SET Value=$value WHERE Key='paused'", ("$value", paused ? "true" : "false"));
    }

    public IReadOnlyList<PrefetchJob> List()
    {
        lock (_gate)
        {
            using var command = Command("SELECT * FROM Jobs ORDER BY Updated DESC,Id LIMIT $limit", ("$limit", _capacity + 256));
            using var reader = command.ExecuteReader();
            var result = new List<PrefetchJob>();
            var now = DateTimeOffset.UtcNow;
            while (reader.Read())
            {
                var job = Read(reader);
                if (job.State == "running")
                {
                    // Include the current run so a running job's average speed is live.
                    if (NullableInt64(reader, "RunStarted") is { } runStarted)
                        job = job with { ActiveMs = (job.ActiveMs ?? 0) + Math.Max(0, now.ToUnixTimeMilliseconds() - runStarted) };
                    if (Rates.Get(job.Id, now) is { } rate)
                        job = job with { RecentBytesPerSecond = rate.RecentBytesPerSecond,
                            LastProgressAt = rate.LastProgressAt.ToUnixTimeMilliseconds(), Stalled = rate.Stalled };
                }
                result.Add(job);
            }
            return result;
        }
    }

    private PrefetchJob? ReadOne(string sql, params (string Key, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
    private static PrefetchJob Read(SqliteDataReader reader) => new(reader.GetString(0), Guid.Parse(reader.GetString(1)),
        reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10))
    {
        StartedAt = NullableInt64(reader, "StartedAt"),
        FinishedAt = NullableInt64(reader, "FinishedAt"),
        ActiveMs = NullableInt64(reader, "ActiveMs"),
        WarmedBytes = NullableInt64(reader, "WarmedBytes"),
        FailureCode = NullableString(reader, "FailureCode"),
        Remedy = NullableString(reader, "Remedy"),
        MissReason = NullableString(reader, "MissReason"),
        ViewerUser = NullableString(reader, "ViewerUser"),
        ViewerPlayer = NullableString(reader, "ViewerPlayer"),
        MediaDurationMs = NullableInt64(reader, "MediaDurationMs"),
    };
    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
    private static long? NullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
    private long NextCreated(long wallClockMilliseconds)
    {
        using var command = Command("SELECT COALESCE(MAX(Created),0) FROM Jobs");
        var latest = (long)command.ExecuteScalar()!;
        return Math.Max(wallClockMilliseconds, checked(latest + 1));
    }
    private SqliteCommand Command(string sql, params (string Key, object Value)[] parameters)
    {
        var command = _database.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        return command;
    }
    private void Execute(string sql, params (string Key, object Value)[] parameters)
    { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    private T Atomic<T>(Func<T> mutation)
    {
        using var transaction = _database.BeginTransaction();
        _transaction = transaction;
        try
        {
            var result = mutation();
            transaction.Commit();
            return result;
        }
        finally { _transaction = null; }
    }
    private void Atomic(Action mutation) => Atomic(() => { mutation(); return true; });
    public void Dispose()
    {
        BlockWireBudget();
        lock (_gate) _database.Dispose();
        _wireBudgetFailure.Dispose();
    }
}
