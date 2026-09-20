using NzbDavMigration.Canary;
using NzbWebDAV.UsenetMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryLinkRollbackTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"canary-rollback-{Guid.NewGuid():N}");

    [Fact]
    public async Task RollbackAsync_RemovesOnlyOwnedStillMatchingLinksAndCreatedEmptyDirectories()
    {
        var library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        var target = Directory.CreateDirectory(Path.Join(_root, "target")).FullName;
        var targetA = await CreateTargetAsync(target, ".ids/a", 4);
        var targetB = await CreateTargetAsync(target, ".ids/b", 4);
        var plan = new NzbDavCanaryPlan(
            1, 9, new string('b', 64), DateTimeOffset.UtcNow, 2, 2, true,
            [
                Link("TV/A/a.mkv", ".ids/a", 4),
                Link("TV/B/b.mkv", ".ids/b", 4),
            ]);
        var planDir = await new NzbDavCanaryPlanWriter().WriteAsync(Path.Join(_root, "plans"), plan);
        var journalPath = Path.Join(_root, "journal.json");
        var applier = new CanaryLinkApplier(_ => true);
        var journal = await applier.ApplyAsync(Path.Join(planDir, "plan.json"), library, target, journalPath);
        var changed = journal.Links.Single(link => link.TargetPath == targetB);
        File.Delete(changed.LinkPath);
        File.CreateSymbolicLink(changed.LinkPath, targetA);

        var result = await new CanaryLinkRollback().RollbackAsync(journalPath);

        Assert.Equal(1, result.RemovedLinks);
        Assert.Equal(1, result.SkippedLinks);
        Assert.False(File.Exists(journal.Links.Single(link => link.TargetPath == targetA).LinkPath));
        Assert.Equal(targetA, new FileInfo(changed.LinkPath).LinkTarget);
        Assert.False(Directory.Exists(Path.Join(library, "TV", "A")));
        Assert.True(File.Exists(targetA));
        Assert.True(File.Exists(targetB));
    }

    [Fact]
    public async Task RollbackAsync_RecoversLinksFromInterruptedPartialApply()
    {
        var library = Directory.CreateDirectory(Path.Join(_root, "partial-library")).FullName;
        var target = Directory.CreateDirectory(Path.Join(_root, "partial-target")).FullName;
        var targetA = await CreateTargetAsync(target, ".ids/c", 4);
        var targetB = await CreateTargetAsync(target, ".ids/d", 4);
        var plan = new NzbDavCanaryPlan(
            1, 10, new string('c', 64), DateTimeOffset.UtcNow, 2, 2, true,
            [Link("TV/C/c.mkv", ".ids/c", 4), Link("TV/D/d.mkv", ".ids/d", 4)]);
        var planDir = await new NzbDavCanaryPlanWriter().WriteAsync(Path.Join(_root, "partial-plans"), plan);
        var journalPath = Path.Join(_root, "partial-journal.json");
        var call = 0;
        var applier = new CanaryLinkApplier(_ => true, path =>
        {
            if (Interlocked.Increment(ref call) == 2)
                File.Delete(path);
        });

        await Assert.ThrowsAsync<FileNotFoundException>(() => applier.ApplyAsync(
            Path.Join(planDir, "plan.json"), library, target, journalPath));
        Assert.True(CanaryPathSafety.PathExistsNoFollow(Path.Join(library, "TV", "C", "c.mkv")));
        Assert.False(CanaryPathSafety.PathExistsNoFollow(Path.Join(library, "TV", "D", "d.mkv")));

        var result = await new CanaryLinkRollback().RollbackAsync(journalPath);

        Assert.Equal(1, result.RemovedLinks);
        Assert.Equal(1, result.SkippedLinks);
        Assert.False(CanaryPathSafety.PathExistsNoFollow(Path.Join(library, "TV", "C", "c.mkv")));
        Assert.True(File.Exists(targetA));
        Assert.False(File.Exists(targetB));
    }

    private static NzbDavCanaryPlanLink Link(string path, string target, long size) =>
        new(path, "/legacy/source", Guid.NewGuid(), size, "exact", "{}", target, "planned");

    private static async Task<string> CreateTargetAsync(string root, string relative, int size)
    {
        var path = Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[size]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
