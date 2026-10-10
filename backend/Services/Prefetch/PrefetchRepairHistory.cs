using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Regrab;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Prefetch;

/// <summary>Current repair follow-up, separate from the original warming attempt.</summary>
public sealed record PrefetchRepairOutcome(string Status, Guid? ReplacementItemId, DateTime? RequestedAt,
    DateTime? CompletedAt);

public sealed record PrefetchRepairHistoryEntry(string DisplayName, PrefetchRepairOutcome Outcome);

public static class PrefetchRepairHistory
{
    public static async Task<IReadOnlyDictionary<Guid, PrefetchRepairHistoryEntry>> LoadAsync(
        DavDatabaseContext database, ConfigManager config, IReadOnlyList<PrefetchJob> jobs,
        Func<DavItem, CancellationToken, Task<string?>> currentGeneration, CancellationToken ct)
    {
        var ids = jobs.Select(job => job.ItemId).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, PrefetchRepairHistoryEntry>();
        var rows = await database.ArrRegrabRequests.AsNoTracking()
            .Where(row => row.DavItemId != null && ids.Contains(row.DavItemId.Value))
            .ToListAsync(ct).ConfigureAwait(false);
        var latest = rows.GroupBy(row => row.DavItemId!.Value)
            .Select(group => group.OrderByDescending(row => row.UpdatedAt).ThenByDescending(row => row.Id).First()).ToArray();
        var roots = LibrarySymlinkGuard.ConfiguredRoots(config);
        var links = new Dictionary<Guid, Guid>();
        foreach (var row in latest.Where(row => row.Status == ArrRegrabStatus.Replaced && row.LibraryPath != null))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var link = LibrarySymlinkGuard.Inspect(row.LibraryPath!, roots);
                if (link.Kind != LibraryLinkInspection.Symlink || link.Target == null) continue;
                var mapped = OrganizedLinksUtil.GetDavItemLink(new SymlinkAndStrmUtil.SymlinkInfo
                { SymlinkPath = row.LibraryPath!, TargetPath = link.Target }, config.GetRcloneMountDir());
                if (mapped is { } value && value.DavItemId != row.DavItemId)
                    links[row.DavItemId!.Value] = value.DavItemId;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { /* An unavailable link cannot establish a replacement identity. */ }
        }
        var replacementIds = links.Values.Distinct().ToArray();
        var items = await database.Items.AsNoTracking().Where(item => replacementIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, ct).ConfigureAwait(false);
        var result = new Dictionary<Guid, PrefetchRepairHistoryEntry>();
        var generationReads = 0;
        foreach (var row in latest)
        {
            var status = row.Status;
            Guid? replacementId = null;
            if (status == ArrRegrabStatus.Replaced)
            {
                status = "replacement-unavailable";
                if (links.TryGetValue(row.DavItemId!.Value, out var id) && items.TryGetValue(id, out var item))
                {
                    status = ArrRegrabStatus.Replaced;
                    replacementId = id;
                    // Bound metadata work. Never fetch source bytes or contact Arr from queue status.
                    if (jobs.Any(job => job.ItemId == id && job.State == "completed" && job.Start == 0 && job.Length == 0)
                        && generationReads++ < 32)
                    {
                        var generation = await currentGeneration(item, ct).ConfigureAwait(false);
                        if (IsWholeFileWarmed(jobs, item, generation, row.RequestedAt)) status = "replacement-warmed";
                    }
                }
            }
            result[row.DavItemId!.Value] = new(row.ReleaseName,
                new(status, replacementId, Utc(row.RequestedAt), Utc(row.CompletedAt)));
        }
        return result;
    }

    private static DateTime? Utc(DateTime? value) => value is { } date
        ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;

    internal static bool IsWholeFileWarmed(IEnumerable<PrefetchJob> jobs, DavItem item, string? generation,
        DateTime? requestedAt) => generation != null && item.FileSize is > 0 && jobs.Any(job =>
        job.ItemId == item.Id && job.State == "completed" && job.Start == 0 && job.Length == 0
        && job.Generation == generation && job.CommittedBytes >= item.FileSize
        && (requestedAt == null || job.FinishedAt >= new DateTimeOffset(DateTime.SpecifyKind(requestedAt.Value,
            DateTimeKind.Utc)).ToUnixTimeMilliseconds()));
}
