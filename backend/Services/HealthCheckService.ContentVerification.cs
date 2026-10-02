using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public partial class HealthCheckService
{
    private const int MaxDamageHintsPerItem = 256;

    /// <summary>
    /// Article ids a caller already found damaged for an item queued through
    /// <see cref="QueueRepairForDamagedReleaseAsync"/>. The next health check samples them on
    /// top of its own spread, so a sparse collision found by a deeper sweep (or by warming or
    /// playback) is confirmed instead of slipping between the lighter health-check samples.
    /// In-memory only: after a restart the health check falls back to its own sample.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string[]> _contentDamageHints = new();

    internal IReadOnlyCollection<string> PeekContentDamageHintsForTests(Guid davItemId) =>
        _contentDamageHints.TryGetValue(davItemId, out var hints) ? hints : [];

    /// <summary>
    /// Reusable hand-off for code that finds an imported release damaged outside the health
    /// check (the imported-content sweep, Smart Prefetch warming, playback): queues the item
    /// for a priority health check that re-verifies the given articles and, when damage is
    /// confirmed, runs the existing repair path — PAR2 reconstruction when enabled and the
    /// recovery blocks suffice, otherwise the Arr remove/blocklist/re-search replacement.
    /// Repairs honour the configured repair window. Returns false when the item no longer
    /// exists or is not a Usenet file.
    /// </summary>
    /// <param name="davItemId">The imported file's DavItem id.</param>
    /// <param name="damagedSegmentIds">Article ids observed foreign or missing, if known.</param>
    /// <param name="reason">One-line operator-facing reason, logged once.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> QueueRepairForDamagedReleaseAsync(
        Guid davItemId,
        IReadOnlyCollection<string>? damagedSegmentIds,
        string reason,
        CancellationToken ct)
    {
        if (damagedSegmentIds is { Count: > 0 })
        {
            _contentDamageHints.AddOrUpdate(
                davItemId,
                _ => damagedSegmentIds.Distinct(StringComparer.Ordinal).Take(MaxDamageHintsPerItem).ToArray(),
                (_, existing) => existing.Concat(damagedSegmentIds)
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxDamageHintsPerItem)
                    .ToArray());
        }

        await using var dbContext = CreateContext();
        var item = await dbContext.Items
            .AsNoTracking()
            .Where(x => x.Id == davItemId && x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => new { x.Path, x.NextHealthCheck, x.HealthRepairPending })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (item is null)
        {
            _contentDamageHints.TryRemove(davItemId, out _);
            return false;
        }

        // A playback-urgent item already skips straight to repair; leave its sentinel alone.
        if (item.NextHealthCheck != DateTimeOffset.UnixEpoch)
        {
            await dbContext.Items
                .Where(x => x.Id == davItemId && x.NextHealthCheck != DateTimeOffset.UnixEpoch)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(i => i.NextHealthCheck, ForcedRecheckSentinel)
                        .SetProperty(i => i.HealthRepairPending, true),
                    ct)
                .ConfigureAwait(false);
        }

        if (!item.HealthRepairPending && item.NextHealthCheck != DateTimeOffset.UnixEpoch)
        {
            Log.Information(
                "Queued repair for {Path}: {Reason}. A priority health check confirms the damage, then repairs " +
                "from PAR2 when possible or requests a replacement from Sonarr/Radarr.",
                item.Path, reason);
        }

        SignalWorkerStateChanged();
        return true;
    }

    /// <summary>
    /// Runs sampled article content verification for one imported file outside the health
    /// check (the imported-content sweep). Returns null when the item is not an imported media
    /// file or its streaming payload is gone. Callers set admission and attribution contexts
    /// on <paramref name="ct"/>.
    /// </summary>
    internal async Task<ArticleContentVerification?> VerifyImportedFileContentAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        ArticleContentSampleBudget budget,
        int concurrency,
        CancellationToken ct)
    {
        if (!FilenameUtil.IsMediaFile(davItem.Name)) return null;
        HealthCheckPayload payload;
        try
        {
            payload = await LoadHealthCheckPayloadAsync(davItem, dbClient, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is MissingFilePayloadException or CorruptedBlobPayloadException)
        {
            return null;
        }

        if (payload.Segments.Count == 0) return null;
        return await new ArticleContentVerifier(_usenetClient)
            .VerifyAsync(
                ToContentTarget(davItem, payload.Segments), budget, concurrency, ct,
                isExcluded: _repairPatchStore.HasUsablePatch)
            .ConfigureAwait(false);
    }

    private static ArticleContentTarget ToContentTarget(DavItem davItem, ConcatenatedSegmentView segments) =>
        new(
            davItem.Name,
            segments.Parts
                .Select(part => new PostedArticleFile(
                    part.SegmentIds, part.SegmentFallbackIds, part.TrustedSegmentRanges))
                .ToList());

    /// <summary>
    /// Samples the item's articles' yEnc headers (on top of the STAT sweep) and throws
    /// <see cref="UsenetContentMismatchException"/> when sampled articles belong to another
    /// post or are missing, which routes into the existing repair path. Only media files the
    /// library imports are verified; archives' non-media members are not DavItems.
    /// </summary>
    private async Task VerifyArticleContentAsync(
        DavItem davItem,
        ConcatenatedSegmentView segments,
        DavNzbFile? nzbFile,
        int concurrency,
        CancellationToken ct)
    {
        if (!FilenameUtil.IsMediaFile(davItem.Name) || segments.Count == 0) return;

        // Recorded holes and corrupt segments were already classified by degraded tolerance,
        // and PAR2-patched segments are served locally; none of them is re-read here.
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        if (nzbFile is not null)
        {
            foreach (var index in (nzbFile.MissingSegmentIndices ?? []).Concat(nzbFile.CorruptSegmentIndices ?? []))
            {
                if (index >= 0 && index < nzbFile.SegmentIds.Length)
                    recorded.Add(nzbFile.SegmentIds[index]);
            }
        }

        bool IsExcluded(string id) => recorded.Contains(id) || _repairPatchStore.HasUsablePatch(id);

        _contentDamageHints.TryGetValue(davItem.Id, out var hints);
        var target = ToContentTarget(davItem, segments);

        ArticleContentVerification result;
        using (ct.SetContext(new HealthCheckAdmissionContext(
                   _healthCheckConnectionGate,
                   HealthCheckAdmissionPriority.Background)))
        {
            result = await new ArticleContentVerifier(_usenetClient)
                .VerifyAsync(
                    target,
                    ArticleContentSampleBudget.HealthCheck,
                    Math.Clamp(concurrency, 1, ArticleContentVerifier.DefaultConcurrency),
                    ct,
                    hints,
                    IsExcluded)
                .ConfigureAwait(false);
        }

        _contentDamageHints.TryRemove(davItem.Id, out _);
        if (!result.IsDamaged) return;

        Log.Warning(
            "Health check found {Path} damaged on Usenet: {Detail}. Repairing it from PAR2 when possible, " +
            "otherwise requesting a replacement.",
            davItem.Path, result.Describe());
        throw new UsenetContentMismatchException(result.DamagedSegmentIds, result.Describe());
    }
}
