using NzbDavMigration.Recovery;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class BatchPackagePlannerTests
{
    [Fact]
    public void Partition_SortsDeterministicallyAndKeepsEveryReleaseWholeExactlyOnce()
    {
        var releases = new[]
        {
            Release("release-c", 'c', 400),
            Release("release-a", 'a', 400),
            Release("release-b", 'b', 400),
        };

        var batches = new BatchPackagePlanner().Partition(releases, maxReleases: 2, maxPayloadBytes: 1_000);

        Assert.Equal([2, 1], batches.Select(batch => batch.Releases.Count));
        Assert.Equal(["release-a", "release-b", "release-c"],
            batches.SelectMany(batch => batch.Releases).Select(release => release.SourceReleaseId));
        Assert.All(releases, release => Assert.Equal(1,
            batches.Count(batch => batch.Releases.Contains(release))));
        Assert.All(batches, batch => Assert.False(batch.IsOversizedSingleRelease));
    }

    [Fact]
    public void Partition_PutsAnOversizedReleaseInOneExplicitlyMarkedBatch()
    {
        var oversized = Release("large", 'a', 1_001);
        var normal = Release("small", 'b', 100);

        var batches = new BatchPackagePlanner().Partition([oversized, normal], 2, 1_000);

        Assert.Equal(2, batches.Count);
        Assert.Equal([oversized], batches[0].Releases);
        Assert.True(batches[0].IsOversizedSingleRelease);
        Assert.Equal([normal], batches[1].Releases);
        Assert.False(batches[1].IsOversizedSingleRelease);
    }

    [Fact]
    public void Partition_CanLimitOnlyTheFirstCanaryBatch()
    {
        var releases = Enumerable.Range(0, 7)
            .Select(index => Release($"release-{index}", (char)('a' + index), 10))
            .ToArray();

        var batches = new BatchPackagePlanner().Partition(releases,
            maxReleases: 4, firstBatchMaxReleases: 2);

        Assert.Equal([2, 4, 1], batches.Select(batch => batch.Releases.Count));
        Assert.Equal(7, batches.SelectMany(batch => batch.Releases).Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchPackagePlanner().Partition(
            releases, maxReleases: 4, firstBatchMaxReleases: 5));
    }

    private static FullRecoveryRelease Release(string id, char digest, long bytes) =>
        new(id, new string(digest, 64), $"{id}.nzb", bytes, []);
}
