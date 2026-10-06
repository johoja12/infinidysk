using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Provenance;

namespace NzbWebDAV.UsenetMigration;

internal sealed record NzbDavBatchValidationCheckpoint(
    int SchemaVersion, string PackageRoot, string PackageDigest, long RunId, string PlanDigest,
    string[] SelectedIds, string[] LibraryPaths, string[] ExactIds);

public sealed partial class UsenetMigrationStore
{
    private static NzbDavBatchValidationCheckpoint ReadValidationCheckpoint(MigrationNzbDavBatch batch)
    {
        NzbDavBatchValidationCheckpoint? checkpoint;
        try { checkpoint = JsonSerializer.Deserialize<NzbDavBatchValidationCheckpoint>(batch.ValidationCheckpointJson!); }
        catch (JsonException exception) { throw new InvalidOperationException("Frozen batch evidence is invalid.", exception); }
        if (checkpoint is null || checkpoint.SelectedIds is null || checkpoint.LibraryPaths is null || checkpoint.ExactIds is null
            || string.IsNullOrWhiteSpace(checkpoint.PackageRoot) || string.IsNullOrWhiteSpace(checkpoint.PlanDigest)
            || checkpoint.SelectedIds.Any(string.IsNullOrWhiteSpace) || checkpoint.LibraryPaths.Any(string.IsNullOrWhiteSpace)
            || checkpoint.SchemaVersion != 1 || checkpoint.PackageDigest != batch.PackageDigest
            || checkpoint.RunId != batch.RunId || checkpoint.SelectedIds.Length != batch.SelectionCount
            || checkpoint.LibraryPaths.Length != batch.SelectionCount
            || checkpoint.SelectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.SelectionCount
            || checkpoint.LibraryPaths.Distinct(StringComparer.Ordinal).Count() != batch.SelectionCount
            || checkpoint.ExactIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != checkpoint.ExactIds.Length
            || checkpoint.ExactIds.Except(checkpoint.SelectedIds, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException("Frozen batch evidence disagrees with its registered package.");
        return checkpoint;
    }

    internal async Task<NzbDavBatchValidationCheckpoint?> GetNzbDavBatchCheckpointAsync(int index, CancellationToken ct = default)
    {
        await using var ctx = ContextFactory();
        var master = await ctx.NzbDavMasters.OrderByDescending(item => item.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (master is null) return null;
        var batch = await ctx.NzbDavBatches.SingleOrDefaultAsync(item => item.MasterId == master.Id && item.BatchIndex == index, ct)
            .ConfigureAwait(false);
        return batch?.ValidationCheckpointJson is null ? null : ReadValidationCheckpoint(batch);
    }

    internal async Task<string> FreezeNzbDavBatchAsync(string masterDigest, int index, string packageRoot,
        string packageDigest, long runId, string planDigest, IReadOnlyList<string> actionableIds, IReadOnlyList<string> selectedIds,
        IReadOnlyList<string> libraryPaths, CancellationToken ct = default)
    {
        await using var ctx = ContextFactory();
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var master = await ctx.NzbDavMasters.SingleAsync(item => item.ManifestDigest == masterDigest, ct).ConfigureAwait(false);
        var batch = await ctx.NzbDavBatches.SingleAsync(item => item.MasterId == master.Id && item.BatchIndex == index, ct)
            .ConfigureAwait(false);
        if (batch.PackageDigest != packageDigest || batch.RunId != runId || selectedIds.Count != batch.SelectionCount
            || libraryPaths.Count != batch.SelectionCount || selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selectedIds.Count
            || libraryPaths.Distinct(StringComparer.Ordinal).Count() != libraryPaths.Count)
            throw new InvalidOperationException("Validation checkpoint does not match the registered batch.");
        if (batch.ValidationCheckpointJson is not null)
        {
            var existing = ReadValidationCheckpoint(batch);
            if (existing.PlanDigest != planDigest || existing.PackageRoot != packageRoot
                || !existing.SelectedIds.SequenceEqual(selectedIds) || !existing.LibraryPaths.SequenceEqual(libraryPaths))
                throw new InvalidOperationException("Existing validation checkpoint differs from this request.");
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return batch.Status;
        }
        if (batch.Status != "running")
            throw new InvalidOperationException("Only an unacknowledged imported batch may enter the validation slot.");
        if (await ctx.NzbDavBatches.AnyAsync(item => item.MasterId == master.Id && item.Id != batch.Id
                && item.Status == "validating", ct).ConfigureAwait(false))
            throw new InvalidOperationException("Only one batch may occupy the validation slot.");
        var session = await ctx.SessionState.SingleAsync(item => item.Id == SessionId, ct).ConfigureAwait(false);
        if (session.SourcePackageRoot != packageRoot || session.CurrentRunId != runId)
            throw new InvalidOperationException("The active import session does not belong to this checkpoint.");
        var exactIds = await ReadTerminalExactIdsAsync(ctx, selectedIds, packageDigest, ct).ConfigureAwait(false);
        if (exactIds.Count != actionableIds.Count || !exactIds.SetEquals(actionableIds))
            throw new InvalidOperationException("The immutable plan actionable count disagrees with terminal correlation evidence.");
        batch.ValidationCheckpointJson = JsonSerializer.Serialize(new NzbDavBatchValidationCheckpoint(
            1, packageRoot, packageDigest, runId, planDigest, selectedIds.ToArray(), libraryPaths.ToArray(),
            exactIds.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        batch.Status = "validating";
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return batch.Status;
    }

    private static async Task<HashSet<string>> ReadTerminalExactIdsAsync(
        UsenetMigrationDbContext ctx, IReadOnlyCollection<string> selectedSourceIds,
        string packageDigest, CancellationToken ct)
    {
        var session = await ctx.SessionState.AsNoTracking()
            .SingleAsync(item => item.Id == SessionId, ct).ConfigureAwait(false);
        if (session.Status != MigrationSessionStatus.Complete)
            throw new InvalidOperationException("The active batch must reach terminal completion first.");
        var activeSubmissions = await ctx.Submissions.AsNoTracking().AnyAsync(
            item => item.State == "pending" || item.State == "submitting" || item.State == "submitted"
                    || item.State == "processing", ct).ConfigureAwait(false);
        if (activeSubmissions)
            throw new InvalidOperationException("The active batch still has submissions in progress.");

        var correlated = await ctx.ReleaseFiles.AsNoTracking()
            .Where(item => item.SourceFileId != null && selectedSourceIds.Contains(item.SourceFileId))
            .Select(item => new { item.SourceFileId, item.StoreRef, item.FileStatus, item.NewDavItemId })
            .ToListAsync(ct).ConfigureAwait(false);
        if (correlated.Count != selectedSourceIds.Count
            || correlated.Select(item => item.SourceFileId!).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != selectedSourceIds.Count)
            throw new InvalidOperationException("Every selected source file must have one correlation row.");
        var storeRefs = correlated.Select(item => item.StoreRef).Distinct().ToArray();
        var submissions = await ctx.Submissions.AsNoTracking()
            .Where(item => storeRefs.Contains(item.StoreRef))
            .ToDictionaryAsync(item => item.StoreRef, ct).ConfigureAwait(false);
        var exactIds = correlated
            .Where(item => item.FileStatus == "exact" && item.NewDavItemId != null
                           && submissions.TryGetValue(item.StoreRef, out var submission)
                           && submission.State is "completed" or "history_cleared")
            .Select(item => item.SourceFileId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var terminalFailedIds = correlated
            .Where(item => item.FileStatus == "import-failed" && item.NewDavItemId is null
                           && submissions.TryGetValue(item.StoreRef, out var submission)
                           && submission.State is "failed" or "evicted")
            .Select(item => item.SourceFileId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatchedIds = correlated
            .Where(item => item.FileStatus == "unmatched-target" && item.NewDavItemId is null
                           && submissions.TryGetValue(item.StoreRef, out var submission)
                           && submission.State is "completed" or "history_cleared")
            .Select(item => item.SourceFileId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scanExcludedIds = await NzbDavScanExclusion.VerifiedSourceIdsAsync(ctx, packageDigest, ct)
            .ConfigureAwait(false);
        if (exactIds.Count + terminalFailedIds.Count + unmatchedIds.Count + scanExcludedIds.Count != selectedSourceIds.Count
            || selectedSourceIds.Any(id => !exactIds.Contains(id)
                                           && !terminalFailedIds.Contains(id)
                                           && !unmatchedIds.Contains(id)
                                           && !scanExcludedIds.Contains(id)))
            throw new InvalidOperationException("Every selected link must be exact or recorded as an unlinked terminal outcome.");
        return exactIds;
    }
}
