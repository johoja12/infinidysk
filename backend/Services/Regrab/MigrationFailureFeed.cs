using Microsoft.EntityFrameworkCore;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Services.Regrab;

/// <summary>
/// Turns failed submissions of the active NzbDav migration batch into
/// <see cref="MigrationFailureRecord"/>s. Only releases imported into a dedicated
/// <c>migration-*</c> category and rejected as damaged or for missing articles qualify.
/// The verified package manifest supplies each failure's library path and original legacy
/// symlink target; it is read only when a new qualifying failure appears.
/// </summary>
public sealed class MigrationFailureFeed(UsenetMigrationStore store, NzbDavPackageReader reader)
{
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task<IReadOnlyList<MigrationFailureRecord>> CollectNewAsync(CancellationToken ct)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        if (!string.Equals(session.SourceType, MigrationSourceTypes.NzbDav, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(session.SourcePackageRoot))
            return [];

        List<(string StoreRef, string? Error)> qualifying;
        await using (var ctx = store.NewContext())
        {
            var failed = await ctx.Submissions.AsNoTracking()
                .Where(s => s.State == "failed")
                .Select(s => new { s.StoreRef, s.Error })
                .ToListAsync(ct).ConfigureAwait(false);
            var candidates = failed
                .Where(s => ArrRegrabService.ClassifyFailureReason(s.Error) is not null)
                .ToList();
            if (candidates.Count == 0)
                return [];
            var refs = candidates.Select(s => s.StoreRef).ToArray();
            var categories = await ctx.Releases.AsNoTracking()
                .Where(r => refs.Contains(r.StoreRef))
                .Select(r => new { r.StoreRef, r.TargetCategory })
                .ToDictionaryAsync(r => r.StoreRef, r => r.TargetCategory, ct).ConfigureAwait(false);
            qualifying = candidates
                .Where(s => categories.TryGetValue(s.StoreRef, out var category)
                            && category is not null
                            && category.StartsWith("migration", StringComparison.OrdinalIgnoreCase))
                .Select(s => (s.StoreRef, s.Error))
                .ToList();
        }

        var root = session.SourcePackageRoot;
        lock (_gate)
            qualifying.RemoveAll(s => _handled.Contains($"{root}\n{s.StoreRef}"));
        if (qualifying.Count == 0)
            return [];

        var package = await reader.ReadAsync(root, ct).ConfigureAwait(false);
        var records = Build(package, qualifying);
        lock (_gate)
        {
            foreach (var (storeRef, _) in qualifying)
                _handled.Add($"{root}\n{storeRef}");
        }

        return records;
    }

    /// <summary>Maps failed <c>nzbdav:{releaseId}</c> submissions to their selected library links.</summary>
    public static IReadOnlyList<MigrationFailureRecord> Build(
        NzbDavVerifiedPackage package,
        IReadOnlyList<(string StoreRef, string? Error)> failures)
    {
        var releases = package.Manifest.Releases.ToDictionary(r => r.SourceReleaseId, StringComparer.Ordinal);
        var releaseByLeaf = package.Manifest.Releases
            .SelectMany(r => r.Leaves.Select(l => (l.LegacyDavItemId, r.SourceReleaseId)))
            .ToDictionary(x => x.LegacyDavItemId, x => x.SourceReleaseId);
        var errors = failures.ToDictionary(f => f.StoreRef, f => f.Error, StringComparer.Ordinal);
        var records = new List<MigrationFailureRecord>();
        foreach (var link in package.Manifest.SelectedLinks)
        {
            if (!releaseByLeaf.TryGetValue(link.LegacyDavItemId, out var releaseId)
                || !errors.TryGetValue($"nzbdav:{releaseId}", out var error))
                continue;
            var release = releases[releaseId];
            records.Add(new MigrationFailureRecord
            {
                LegacyDavItemId = link.LegacyDavItemId.ToString("D"),
                LibraryRelativePath = link.LibraryRelativePath,
                OriginalTarget = link.OriginalTarget,
                Reason = error,
                SubmissionState = "failed",
                SourceReleaseId = releaseId,
                ReleaseName = release.SourceJobName ?? release.SourceFileName,
                BatchIndex = package.Manifest.BatchIndex,
            });
        }

        return records;
    }
}
