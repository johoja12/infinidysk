using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav.Base;
using Serilog;

namespace NzbWebDAV.Services.Prefetch;

/// <summary>Stable codes stored on a job for its latest failure or deferral.</summary>
public static class PrefetchFailureCodes
{
    /// <summary>Articles are missing, corrupt, or belong to a different post on every provider.</summary>
    public const string SourceDamaged = "source-damaged";
    /// <summary>Source bytes arrived without integrity proof; repeated failures escalate to damage.</summary>
    public const string SourceUnverified = "source-unverified";
    /// <summary>The source could not be read for a transient reason.</summary>
    public const string SourceUnavailable = "source-unavailable";
    /// <summary>The cache folder could not commit verified bytes.</summary>
    public const string CacheStorage = "cache-storage";
    public const string SourceChanged = "source-changed";
    public const string Budget = "budget";
    public const string Busy = "busy";
}

/// <summary>Follow-up taken when a job fails because its release is damaged.</summary>
public static class PrefetchRemedies
{
    public const string RepairQueued = "repair-queued";
    public const string RepairPending = "repair-pending";
    public const string RepairDisabled = "repair-disabled";
    public const string RepairUnavailable = "repair-unavailable";
}

public sealed class PrefetchDeferredException(string message, bool countsAsFailure = false, string? failureCode = null) : Exception(message)
{
    public bool CountsAsFailure { get; } = countsAsFailure;
    public string? FailureCode { get; } = failureCode;
}

/// <summary>Warming cannot succeed by retrying: the job fails at once with a stable code and remedy.</summary>
public sealed class PrefetchFailedException(string failureCode, string message, string? remedy = null) : Exception(message)
{
    public string FailureCode { get; } = failureCode;
    public string? Remedy { get; } = remedy;
}

/// <summary>Conclusive evidence that the release itself is damaged on Usenet.</summary>
public sealed class PrefetchSourceDamagedException(string message, string? segmentId) : Exception(message)
{
    public string? SegmentId { get; } = segmentId;
}

/// <summary>Extra streams a warming job may fill out of order beside its primary stream.</summary>
/// <param name="Count">Extra lanes beyond the primary one.</param>
/// <param name="Open">Opens one more stream for the same file; null when none can be admitted.</param>
/// <param name="MayStart">Whether an extra lane may start or take another chunk now; while false it waits and rejoins later.</param>
public sealed record WarmLanes(int Count, Func<CancellationToken, Task<NativeCachedStream?>> Open, Func<bool> MayStart)
{
    /// <summary>How often a paused extra lane checks whether it may rejoin.</summary>
    public TimeSpan ResumeDelay { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed class NativePrefetchExecutor(IServiceScopeFactory scopes, NativeCacheService native, ConfigManager config,
    PrefetchJobStore jobs, ActiveReadRegistry activeReads, PlexPlaybackRegistry? playback = null,
    Func<PrefetchSettings>? settingsProvider = null) : IPrefetchExecutor
{
    /// <summary>How long a damaged-release verdict keeps routine policies from re-enqueueing the item.</summary>
    public static readonly TimeSpan DamagedCooldown = TimeSpan.FromHours(24);
    private static readonly TimeSpan GovernorInterval = TimeSpan.FromSeconds(1);
    private PrefetchSettings Settings() => settingsProvider?.Invoke()
        ?? PrefetchSettings.Parse(config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings));
    public async Task ExecuteAsync(PrefetchJob job, CancellationToken ct)
    {
        var settings = Settings();
        if (native.Store is null) throw new PrefetchDeferredException("Native cache must be active before warming.");
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var item = await database.GetFileById(job.ItemId.ToString()).ConfigureAwait(false)
            ?? throw new ArgumentException("The imported media file no longer exists.");
        if (item.FileSize is not > 0 || item.FileSize > settings.MaxBytesPerItem)
            throw new ArgumentException("The media length is unavailable or exceeds the per-file warming cap.");
        if (item.FileSize < NativeCacheSettings.MinimumFileBytes(config))
            throw new ArgumentException("The media file is below the configured Native Cache minimum size.");
        var factory = scope.ServiceProvider.GetRequiredService<IDavContentStreamFactory>();
        await using var wireBudget = new PrefetchWireBudget(jobs, () => Settings().DailyByteBudget, ct);
        using var attribution = wireBudget.Enter();
        string? generation = null;
        try
        {
        var usenet = scope.ServiceProvider.GetService<NzbWebDAV.Clients.Usenet.UsenetStreamingClient>();
        await using var governor = new WarmConnectionGovernor(settings.ConnectionsPerJob,
            () => usenet?.GetProviderConnectionSnapshots() ?? [], GovernorInterval);
        await using var readScope = BaseStoreStreamFile.BeginWarmReadScope(config, scope.ServiceProvider,
            governor.Semaphore, wireBudget.Token);
        Stream admitted;
        try { admitted = await native.WrapAsync(item, token => factory.OpenAsync(item, token), wireBudget.Token, requireNative: true).ConfigureAwait(false); }
        catch (InvalidOperationException) { throw new PrefetchDeferredException("Native cache buffers or metadata are unavailable."); }
        await using var stream = admitted;
        var cached = (NativeCachedStream)stream;
        generation = cached.Identity.Generation;
        bool CanContinue()
        {
            var current = Settings();
            return !jobs.Paused && jobs.IsRunning(job.Id)
                && item.FileSize <= current.MaxBytesPerItem
                && (!current.PauseDuringPlayback || activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true);
        }
        // A manual request is an explicit ask to prove the cache, so it hashes every block.
        var sampling = jobs.HasOwner(job.Id, "manual") ? null : VerificationSample.ForRun(DateTimeOffset.UtcNow);
        // Bytes this run filled since the last progress write; flushed with the next coverage update.
        var unreportedWarmed = 0L;
        var lanes = WarmLanesFor(item, factory);
        try
        {
            await WarmAsync(native.Store, cached, job.Start, job.Length,
                async _ => CanContinue() && await wireBudget.PrepareReadAsync(ct).ConfigureAwait(false),
                bytes =>
                {
                    jobs.Progress(job.Id, cached.Identity.Generation, bytes, Interlocked.Exchange(ref unreportedWarmed, 0));
                }, wireBudget.Token,
                CanContinue, native.ActiveSettings?.ChunkMb ?? 64, sampling,
                warmed => Interlocked.Add(ref unreportedWarmed, warmed), lanes).ConfigureAwait(false);
        }
        catch (PrefetchSourceDamagedException damaged) when (!ct.IsCancellationRequested)
        {
            throw ReportDamaged(scope.ServiceProvider, item, job, generation, damaged.SegmentId, damaged.Message);
        }
        // The same block failing verification on every retry is damage, not bad luck.
        catch (PrefetchDeferredException deferred) when (!ct.IsCancellationRequested
            && deferred.FailureCode == PrefetchFailureCodes.SourceUnverified && jobs.Attempts(job.Id) >= settings.MaxRetries)
        {
            throw ReportDamaged(scope.ServiceProvider, item, job, generation, null,
                "The same source bytes failed verification on every retry: articles are missing, corrupt, or belong to a different post.");
        }
        jobs.RecordVerified(job.ItemId, cached.Identity.Generation, job.Start, job.Length);
        }
        catch (NativeCacheBusyException)
        {
            throw new PrefetchDeferredException("Native cache buffers are busy with playback; warming resumes later.",
                failureCode: PrefetchFailureCodes.Busy);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not PrefetchFailedException
            && (wireBudget.Exceeded || jobs.WireBudgetBlocked) && !ct.IsCancellationRequested)
        {
            throw new PrefetchDeferredException(wireBudget.AccountingFailed || jobs.WireBudgetBlocked
                ? "Warming accounting failed. Repair local metadata storage and restart; playback remains available."
                : "Daily provider-payload warming budget exhausted; verified coverage retained.", failureCode: PrefetchFailureCodes.Budget);
        }
    }

    /// <summary>Most streams one job fills at once.</summary>
    public const int MaxLanes = 4;

    /// <summary>
    /// Extra lanes for one job: half the native buffer slots, at most <see cref="MaxLanes"/> in all.
    /// They run only while nothing is playing and at least half the slots are free, so playback
    /// always finds buffers; the primary lane alone continues otherwise.
    /// </summary>
    private WarmLanes? WarmLanesFor(NzbWebDAV.Database.Models.DavItem item, IDavContentStreamFactory factory)
    {
        if (native.BufferSlots is not { } slots) return null;
        var extra = Math.Clamp(slots.Capacity / 2, 1, MaxLanes) - 1;
        if (extra == 0) return null;
        return new WarmLanes(extra,
            async token => await native.WrapAsync(item, open => factory.OpenAsync(item, open), token, requireNative: true)
                .ConfigureAwait(false) as NativeCachedStream,
            () => activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true
                && slots.Free >= (slots.Capacity + 1) / 2);
    }

    /// <summary>
    /// A damaged release cannot be fixed by warming again: hand the item to the existing repair path
    /// (the same urgent health-check scheduling playback failures use), remember the verdict so policy
    /// refreshes stop re-enqueueing it, and fail the job with an actionable reason.
    /// </summary>
    private PrefetchFailedException ReportDamaged(IServiceProvider services, NzbWebDAV.Database.Models.DavItem item,
        PrefetchJob job, string? generation, string? segmentId, string detail)
    {
        var outcome = services.GetService<NzbWebDAV.Services.StreamingRepairScheduler>()?.Schedule(item, segmentId);
        var (remedy, followUp) = outcome switch
        {
            NzbWebDAV.Services.RepairScheduleOutcome.Scheduled or NzbWebDAV.Services.RepairScheduleOutcome.AlreadyScheduled =>
                (PrefetchRemedies.RepairQueued, "Queued for repair."),
            NzbWebDAV.Services.RepairScheduleOutcome.BelowThreshold =>
                (PrefetchRemedies.RepairPending, "Repair starts once the failure threshold in Settings > Health & Repairs is reached."),
            NzbWebDAV.Services.RepairScheduleOutcome.Disabled =>
                (PrefetchRemedies.RepairDisabled, $"Automatic repair is off: {config.GetRepairDisabledReason()}."),
            _ => (PrefetchRemedies.RepairUnavailable, "Automatic repair is unavailable; check the file from Health."),
        };
        try { jobs.MarkDamaged(item.Id, generation, DateTimeOffset.UtcNow.Add(DamagedCooldown)); }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        { Log.Debug(exception, "Could not record the damaged-release verdict for {ItemId}", item.Id); }
        Log.Warning("Smart Prefetch job {JobId} for item {ItemId}: release damaged on Usenet. {Reason} {Remedy}",
            job.Id, item.Id, detail, followUp);
        return new PrefetchFailedException(PrefetchFailureCodes.SourceDamaged,
            $"Release damaged on Usenet. {detail} {followUp} Routine warming skips this file until it is repaired or for 24 hours.", remedy);
    }
    public static Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, bool> spend, Action<long> progress, CancellationToken ct, int chunkMb = 64)
        => WarmAsync(store, stream, start, length, bytes => new ValueTask<bool>(spend(bytes)),
            progress, ct, chunkMb: chunkMb);

    public static async Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, ValueTask<bool>> spend, Action<long> progress, CancellationToken ct,
        Func<bool>? canContinue = null, int chunkMb = 64, VerificationSample? sampling = null,
        Action<long>? warmed = null, WarmLanes? lanes = null)
    {
        if (start < 0 || start >= stream.Length || length < 0 || length > stream.Length - start)
            throw new ArgumentException("The warm range is outside the media file.");
        if (chunkMb is < 4 or > 256 || chunkMb % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(chunkMb));
        var end = length == 0 ? stream.Length : start + length;
        var (position, alignedEnd) = AlignedRange(start, length, stream.Length);
        if (canContinue?.Invoke() == false) throw new PrefetchDeferredException("Warming is paused or foreground playback has priority.", failureCode: PrefetchFailureCodes.Busy);
        var initialMissing = await store.GetMissingRangeBytesAsync(stream.Identity, position, alignedEnd, ct).ConfigureAwait(false);
        await store.RestartPartialWarmAsync(stream.Identity, initialMissing, ct).ConfigureAwait(false);
        // A damaged sampled block suggests more damage nearby, so that run falls back to hashing every block.
        if (await VerifyExistingRangesAsync(store, stream, position, alignedEnd, canContinue, progress, sampling, ct).ConfigureAwait(false)
            && sampling is not null)
            await VerifyExistingRangesAsync(store, stream, position, alignedEnd, canContinue, progress, null, ct).ConfigureAwait(false);
        var missing = await store.GetMissingRangeBytesAsync(stream.Identity, position, alignedEnd, ct).ConfigureAwait(false);
        if (missing == 0)
        {
            if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before completion.", failureCode: PrefetchFailureCodes.SourceChanged);
            progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
            return;
        }
        var chunkBytes = chunkMb * 1024L * 1024L;
        if (lanes is { Count: > 0 })
            await WarmInLanesAsync(store, stream, position, end, alignedEnd, chunkBytes, lanes, spend, progress, warmed, ct).ConfigureAwait(false);
        else
        {
            // One stream keeps its source pipeline across chunk boundaries.
            var lane = new WarmLane(stream);
            for (; position < end; position = Math.Min(alignedEnd, position + chunkBytes))
                await WarmChunkAsync(store, lane, position, Math.Min(alignedEnd, position + chunkBytes), alignedEnd,
                    spend, progress, warmed, stop: null, shared: null, ct).ConfigureAwait(false);
        }
        if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before completion.", failureCode: PrefetchFailureCodes.SourceChanged);
        progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
    }

    /// <summary>One stream filling chunks, with the uncached run its source pipeline is filling.</summary>
    private sealed class WarmLane(NativeCachedStream stream)
    {
        public NativeCachedStream Stream { get; } = stream;
        public byte[] Probe { get; } = new byte[1];
        public long RunEnd { get; set; } = -1;
        public long NextSequential { get; set; } = -1;
    }

    /// <summary>
    /// Fills the missing blocks of one chunk in order. <paramref name="windowLimit"/> bounds the
    /// source window: the range end for a single stream, the chunk end for a lane, so a lane never
    /// prefetches bytes another lane is filling. Returns early once <paramref name="stop"/> is set.
    /// </summary>
    private static async Task WarmChunkAsync(NativeCacheStore store, WarmLane lane, long chunkStart, long chunkEnd,
        long windowLimit, Func<long, ValueTask<bool>> spend, Action<long> progress, Action<long>? warmed,
        Func<bool>? stop, IDisposable? shared, CancellationToken ct)
    {
        var stream = lane.Stream;
        var chunkMissing = await store.GetMissingRangeBytesAsync(stream.Identity, chunkStart, chunkEnd, ct).ConfigureAwait(false);
        if (chunkMissing == 0) return;
        // Lanes of one file share its single warm reservation and grow it chunk by chunk.
        using var owned = shared is null ? await store.ReserveWarmAsync(stream.Identity, chunkMissing, ct).ConfigureAwait(false) : null;
        if (shared is null ? owned is null : !await store.TryExtendWarmAsync(shared, chunkMissing, ct).ConfigureAwait(false))
            throw new PrefetchDeferredException("Cache storage: no writable folder has enough unreserved capacity for this warming chunk.", failureCode: PrefetchFailureCodes.CacheStorage);
        var reservation = owned ?? shared!;
        var position = chunkStart;
        while (position < chunkEnd)
        {
            if (stop?.Invoke() == true) return;
            position = await store.FindNextMissingOffsetAsync(stream.Identity, position, chunkEnd, ct).ConfigureAwait(false);
            if (position >= chunkEnd) break;
            ct.ThrowIfCancellationRequested();
            if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed; verified coverage must be rechecked.", failureCode: PrefetchFailureCodes.SourceChanged);
            var count = Math.Min(NativeCacheStore.BlockSize, stream.Length - position);
            if (!await spend(count).ConfigureAwait(false)) throw new PrefetchDeferredException("Daily warming budget exhausted or foreground playback has priority.", failureCode: PrefetchFailureCodes.Budget);
            // A skip past blocks cached meanwhile reseeks the source, so the run is measured again.
            if (position >= lane.RunEnd || position != lane.NextSequential)
            {
                lane.RunEnd = await store.FindNextCachedOffsetAsync(stream.Identity, position, windowLimit, ct).ConfigureAwait(false);
                stream.WarmWindowEnd = lane.RunEnd;
            }
            stream.Position = position;
            int probed;
            try { probed = await stream.ReadWarmProbeAsync(lane.Probe, ct).ConfigureAwait(false); }
            catch (Exception exception) when (!ct.IsCancellationRequested && ConclusiveDamage(exception, out var segmentId))
            { throw new PrefetchSourceDamagedException(DamagedDetail(position), segmentId); }
            if (probed != 1 || !stream.LastReadCacheable || !stream.IsSourceCurrent
                || await store.FindNextMissingOffsetAsync(stream.Identity, position, Math.Min(position + count, chunkEnd), ct).ConfigureAwait(false) == position)
                throw ClassifyProbeFailure(store, stream, reservation, position, probed == 1);
            warmed?.Invoke(count);
            progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
            position = Math.Min(position + count, chunkEnd);
            lane.NextSequential = position;
        }
    }

    /// <summary>
    /// Fills chunks out of order: the primary stream and up to <see cref="WarmLanes.Count"/> extra
    /// streams each take the next unfinished chunk, so a slow article stalls only its own lane.
    /// Extra lanes start, and take each further chunk, only while <see cref="WarmLanes.MayStart"/>
    /// allows; the primary lane always runs. The first failure stops every lane at its next block
    /// and is rethrown with its original classification.
    /// </summary>
    private static async Task WarmInLanesAsync(NativeCacheStore store, NativeCachedStream primary, long position, long end,
        long alignedEnd, long chunkBytes, WarmLanes lanes, Func<long, ValueTask<bool>> spend, Action<long> progress,
        Action<long>? warmed, CancellationToken ct)
    {
        using var shared = await store.ReserveWarmAsync(primary.Identity, 0, ct).ConfigureAwait(false)
            ?? throw new PrefetchDeferredException("Cache storage: no writable folder has enough unreserved capacity for this warming chunk.", failureCode: PrefetchFailureCodes.CacheStorage);
        var gate = new object();
        var next = position;
        Exception? failure = null;
        bool Stopped() => Volatile.Read(ref failure) is not null;
        var laneCount = lanes.Count + 1;
        bool HasWork() { lock (gate) return next < end && !Stopped(); }
        bool TryTake(out long chunkStart, out long chunkEnd)
        {
            lock (gate)
            {
                chunkStart = next;
                chunkEnd = next + TailPiece(alignedEnd - next, chunkBytes, laneCount);
                if (next >= end || Stopped()) return false;
                next = chunkEnd;
                return true;
            }
        }
        // An extra lane yields to playback or short buffers by waiting, not retiring, and
        // rejoins while unassigned chunks remain. False once there is nothing left to take.
        async Task<bool> WaitToRunAsync()
        {
            while (!lanes.MayStart())
            {
                if (!HasWork()) return false;
                await Task.Delay(lanes.ResumeDelay, ct).ConfigureAwait(false);
            }
            return HasWork();
        }
        async Task RunAsync(WarmLane lane, bool extra)
        {
            while ((!extra || await WaitToRunAsync().ConfigureAwait(false)) && TryTake(out var chunkStart, out var chunkEnd))
                await WarmChunkAsync(store, lane, chunkStart, chunkEnd, chunkEnd, spend, progress, warmed, Stopped, shared, ct).ConfigureAwait(false);
        }
        async Task LaneAsync(bool extra)
        {
            NativeCachedStream? opened = null;
            try
            {
                if (extra)
                {
                    if (!await WaitToRunAsync().ConfigureAwait(false)) return;
                    try { opened = await lanes.Open(ct).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        Log.Debug(exception, "An extra warming lane could not open; the remaining lanes continue");
                        return;
                    }
                    if (opened is null) return;
                    if (opened.Identity != primary.Identity) return;
                }
                await RunAsync(new WarmLane(opened ?? primary), extra).ConfigureAwait(false);
            }
            catch (Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); }
            finally { if (opened is not null) await opened.DisposeAsync().ConfigureAwait(false); }
        }
        var running = new List<Task> { LaneAsync(extra: false) };
        for (var index = 0; index < lanes.Count; index++) running.Add(Task.Run(() => LaneAsync(extra: true), CancellationToken.None));
        await Task.WhenAll(running).ConfigureAwait(false);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Smallest piece the tail of a laned warm is split into.</summary>
    internal const long MinTailPiece = 2L * NativeCacheStore.BlockSize;

    /// <summary>
    /// Bytes the next lane takes from <paramref name="remaining"/> unassigned bytes: a whole chunk
    /// while there is a chunk for every lane, then an equal share per lane rounded up to whole
    /// blocks (at least <see cref="MinTailPiece"/>), so every lane stays busy to the end.
    /// </summary>
    internal static long TailPiece(long remaining, long chunkBytes, int lanes)
    {
        if (remaining <= 0) return 0;
        if (remaining >= chunkBytes * lanes) return Math.Min(chunkBytes, remaining);
        var share = (remaining + lanes - 1) / lanes;
        share = (share + NativeCacheStore.BlockSize - 1) / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        return Math.Min(remaining, Math.Min(chunkBytes, Math.Max(MinTailPiece, share)));
    }

    private static string DamagedDetail(long position) =>
        $"Articles near {position / (1024 * 1024)} MiB are missing, corrupt, or belong to a different post on every provider.";

    /// <summary>True for a definitive article miss (every provider answered) or corrupt payload.</summary>
    private static bool ConclusiveDamage(Exception exception, out string? segmentId)
    {
        segmentId = null;
        if (exception.TryGetCausingException(out UsenetArticleNotFoundException? miss) && miss!.InconclusiveReason is null)
        {
            segmentId = miss.SegmentId;
            return true;
        }
        if (exception.TryGetCausingException(out UsenetCorruptArticleException? corrupt))
        {
            segmentId = corrupt!.SegmentId;
            return true;
        }
        return false;
    }

    /// <summary>Why a warmed block did not end up cached, as an actionable, classified exception.</summary>
    private static Exception ClassifyProbeFailure(NativeCacheStore store, NativeCachedStream stream, IDisposable reservation,
        long position, bool read)
    {
        if (!stream.IsSourceCurrent)
            return new PrefetchDeferredException("Source changed; verified coverage must be rechecked.", failureCode: PrefetchFailureCodes.SourceChanged);
        if (read && stream.LastReadCacheable)
        {
            var folder = store.ReservationFolderName(reservation);
            return new PrefetchDeferredException(
                $"Cache storage error: verified bytes could not be committed{(folder is null ? "" : $" to the cache folder \"{folder}\"")}. Check that folder's free space, permissions, and mount.",
                countsAsFailure: true, PrefetchFailureCodes.CacheStorage);
        }
        if (stream.LastFillFailure is { } failure && ConclusiveDamage(failure, out var segmentId))
            return new PrefetchSourceDamagedException(DamagedDetail(position), segmentId);
        if (stream.LastUnverifiedSourceBlock == position / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize)
            return new PrefetchDeferredException(
                $"Source bytes near {position / (1024 * 1024)} MiB did not verify: articles may be missing, corrupt, or from a different post. Retrying; repeated failures are treated as a damaged release.",
                countsAsFailure: true, PrefetchFailureCodes.SourceUnverified);
        return new PrefetchDeferredException("Source bytes were not verified or the cache could not commit this range.",
            countsAsFailure: true, PrefetchFailureCodes.SourceUnavailable);
    }

    /// <summary>The requested range widened to whole integrity blocks; a length of 0 means "to the end of the file".</summary>
    public static (long Start, long End) AlignedRange(long start, long length, long fileLength)
    {
        var end = length == 0 ? fileLength : start + length;
        var alignedStart = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        var alignedEnd = end % NativeCacheStore.BlockSize == 0 ? end
            : end + Math.Min(NativeCacheStore.BlockSize - end % NativeCacheStore.BlockSize, fileLength - end);
        return (alignedStart, alignedEnd);
    }

    /// <summary>
    /// True when warming these ranges would only re-read cache that a completed warm already verified:
    /// every range is fully catalogued for the current generation and was verified since <paramref name="since"/>.
    /// A different generation (changed source) never matches, so it is warmed normally.
    /// </summary>
    public static async Task<bool> IsRecentlyWarmAsync(NativeCacheStore store, PrefetchJobStore jobs, NativeCacheIdentity identity,
        Guid itemId, IEnumerable<(long Start, long Length)> ranges, DateTimeOffset since, CancellationToken ct)
    {
        var any = false;
        foreach (var (start, length) in ranges)
        {
            if (start < 0 || length < 0 || start >= identity.Length || length > identity.Length - start) return false;
            if (!jobs.WasVerifiedSince(itemId, identity.Generation, start, length, since)) return false;
            var (alignedStart, alignedEnd) = AlignedRange(start, length, identity.Length);
            if (await store.GetMissingRangeBytesAsync(identity, alignedStart, alignedEnd, ct).ConfigureAwait(false) != 0) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Hashes the job's catalogued blocks, or only a <paramref name="sampling"/> of them.
    /// Returns true when a block failed and was invalidated for refill.
    /// </summary>
    private static async Task<bool> VerifyExistingRangesAsync(NativeCacheStore store, NativeCachedStream stream,
        long start, long end, Func<bool>? canContinue, Action<long> progress, VerificationSample? sampling, CancellationToken ct)
    {
        // Catalogue coverage is a snapshot. It cannot prove the mounted data still
        // exists or matches its committed hash. Validate only this job's blocks,
        // with bounded catalogue pages and the stream's existing buffer admission.
        // Progress reports the catalogued bytes walked so far; a large cached file otherwise
        // shows no movement for the whole pass.
        const long progressInterval = 64L * 1024 * 1024;
        var blockCount = (end - start + NativeCacheStore.BlockSize - 1) / NativeCacheStore.BlockSize;
        var checkedBytes = 0L;
        var reported = 0L;
        var failed = false;
        var after = start - 1;
        while (true)
        {
            var ranges = await store.ListVerifiedRangesAsync(stream.Identity.Key, after, 100, ct).ConfigureAwait(false);
            if (ranges.Count == 0) return failed;
            foreach (var range in ranges)
            {
                if (range.Offset >= end) return failed;
                ct.ThrowIfCancellationRequested();
                if (range.Offset >= start && sampling is not null
                    && !sampling.Includes((range.Offset - start) / NativeCacheStore.BlockSize, blockCount))
                {
                    checkedBytes += range.Count;
                    if (checkedBytes - reported >= progressInterval) { progress(checkedBytes); reported = checkedBytes; }
                    after = range.Offset;
                    continue;
                }
                if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before cache verification.", failureCode: PrefetchFailureCodes.SourceChanged);
                if (canContinue?.Invoke() == false)
                    throw new PrefetchDeferredException("Warming is paused or foreground playback has priority.", failureCode: PrefetchFailureCodes.Busy);
                if (!await stream.VerifyCachedBlockAsync(range.Offset, ct).ConfigureAwait(false))
                {
                    // Missing/truncated/corrupt data invalidates the indexed block
                    // and is filled by the ordinary budgeted path below. An offline
                    // or inaccessible volume leaves coverage intact; defer it.
                    if (!stream.IsSourceCurrent || await store.FindNextMissingOffsetAsync(stream.Identity,
                        range.Offset, range.Offset + range.Count, ct).ConfigureAwait(false) != range.Offset)
                        throw new PrefetchDeferredException("Cache storage error: cached storage is unavailable; completion could not be verified.", failureCode: PrefetchFailureCodes.CacheStorage);
                    failed = true;
                }
                else
                {
                    checkedBytes += range.Count;
                    if (reported == 0 || checkedBytes - reported >= progressInterval) { progress(checkedBytes); reported = checkedBytes; }
                }
                after = range.Offset;
            }
        }
    }
}

/// <summary>
/// The cached blocks a routine recheck hashes instead of every block: the head and tail that
/// playback reaches first, plus a bounded evenly strided sample whose starting offset rotates
/// daily so successive rechecks cover different blocks. Playback still hashes every block it
/// serves, so an unsampled bad block is caught and refetched when it is read.
/// </summary>
public sealed record VerificationSample(long Seed)
{
    public const int HeadBlocks = 4;
    public const int TailBlocks = 2;
    public const int StridedPercent = 2;
    public const int MinStridedBlocks = 16;
    public const int MaxStridedBlocks = 128;

    public static VerificationSample ForRun(DateTimeOffset now) => new(now.ToUnixTimeSeconds() / 86400);

    public bool Includes(long blockIndex, long blockCount)
    {
        if (blockIndex < 0 || blockIndex >= blockCount) return false;
        if (blockIndex < HeadBlocks || blockIndex >= blockCount - TailBlocks) return true;
        var strided = Math.Clamp((blockCount * StridedPercent + 99) / 100, MinStridedBlocks, MaxStridedBlocks);
        var stride = Math.Max(1, blockCount / strided);
        var first = (long)((ulong)Seed % (ulong)stride);
        var step = blockIndex - first;
        return step >= 0 && step % stride == 0 && step / stride < strided;
    }
}
