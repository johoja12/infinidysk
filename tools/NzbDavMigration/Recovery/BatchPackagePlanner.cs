namespace NzbDavMigration.Recovery;

public sealed record FullRecoveryRelease(
    string SourceReleaseId,
    string PayloadSha256,
    string PayloadRelativePath,
    long PayloadBytes,
    IReadOnlyList<LegacySourceRecoveryItem> Items);

public sealed record FullRecoveryBatch(
    int BatchIndex,
    IReadOnlyList<FullRecoveryRelease> Releases,
    long PayloadBytes,
    bool IsOversizedSingleRelease);

public sealed class BatchPackagePlanner
{
    public IReadOnlyList<FullRecoveryBatch> Partition(
        IReadOnlyList<FullRecoveryRelease> releases,
        int maxReleases = 250,
        long maxPayloadBytes = 4L * 1024 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReleases);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        var ordered = releases.OrderBy(release => release.PayloadSha256, StringComparer.Ordinal)
            .ThenBy(release => release.SourceReleaseId, StringComparer.Ordinal).ToArray();
        if (ordered.Any(release => release.PayloadBytes < 0))
            throw new InvalidDataException("Recovery release payload sizes cannot be negative.");
        if (ordered.Select(release => release.SourceReleaseId).Distinct(StringComparer.Ordinal).Count()
            != ordered.Length)
            throw new InvalidDataException("Recovery releases contain duplicate source IDs.");

        var batches = new List<FullRecoveryBatch>();
        var current = new List<FullRecoveryRelease>();
        long currentBytes = 0;
        foreach (var release in ordered)
        {
            if (release.PayloadBytes > maxPayloadBytes)
            {
                Flush();
                batches.Add(new FullRecoveryBatch(batches.Count, [release], release.PayloadBytes, true));
                continue;
            }
            if (current.Count == maxReleases || currentBytes > maxPayloadBytes - release.PayloadBytes)
                Flush();
            current.Add(release);
            currentBytes += release.PayloadBytes;
        }
        Flush();
        return batches;

        void Flush()
        {
            if (current.Count == 0) return;
            batches.Add(new FullRecoveryBatch(batches.Count, current.ToArray(), currentBytes, false));
            current.Clear();
            currentBytes = 0;
        }
    }
}
