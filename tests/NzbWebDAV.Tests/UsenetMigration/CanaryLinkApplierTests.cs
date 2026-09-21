using NzbDavMigration.Canary;
using NzbWebDAV.UsenetMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryLinkApplierTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"canary-apply-{Guid.NewGuid():N}");

    [Fact]
    public void LocalMountDetection_AcceptsTemporaryFilesystem()
    {
        Directory.CreateDirectory(_root);
        Assert.True(CanaryPathSafety.IsLocalMount(_root));
    }

    [Theory]
    [InlineData("../escape.mkv")]
    [InlineData("/absolute.mkv")]
    public async Task ApplyAsync_RejectsPathsOutsideLibraryRoot(string libraryPath)
    {
        var fixture = await CreateFixtureAsync(libraryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(File.Exists(Path.Join(_root, "escape.mkv")));
    }

    [Fact]
    public async Task ApplyAsync_RejectsSymlinkedParent()
    {
        var fixture = await CreateFixtureAsync("TV/Show/episode.mkv");
        var outside = Directory.CreateDirectory(Path.Join(_root, "outside")).FullName;
        Directory.CreateSymbolicLink(Path.Join(fixture.LibraryRoot, "TV"), outside);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_NeverOverwritesExistingUnownedObject(bool symlink)
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");
        var output = Path.Join(fixture.LibraryRoot, "TV", "episode.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (symlink)
            File.CreateSymbolicLink(output, Path.Join(_root, "different-target"));
        else
            await File.WriteAllTextAsync(output, "do not replace");

        await Assert.ThrowsAsync<IOException>(() => fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.True(symlink ? new FileInfo(output).LinkTarget is not null : File.Exists(output));
    }

    [Fact]
    public async Task ApplyAsync_IsIdempotentOnlyForLinkOwnedBySameJournal()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");
        await fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath);

        var retry = await fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath);

        var entry = Assert.Single(retry.Links);
        Assert.Equal("applied", entry.Status);
        Assert.Equal(entry.TargetPath, new FileInfo(entry.LinkPath).LinkTarget);
    }

    [Fact]
    public async Task ApplyAsync_RevalidatesTargetImmediatelyBeforeCreatingLink()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv", beforeCreate: File.Delete);

        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(File.Exists(Path.Join(fixture.LibraryRoot, "TV", "episode.mkv")));
    }

    [Fact]
    public async Task ApplyAsync_RejectsNonLocalLibraryRootBeforeMutation()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");

        await Assert.ThrowsAsync<InvalidDataException>(() => new CanaryLinkApplier(_ => false).ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(File.Exists(Path.Join(fixture.LibraryRoot, "TV", "episode.mkv")));
    }

    [Fact]
    public async Task ApplyAsync_RejectsSourceLinkChangedSincePlan()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");
        File.Delete(fixture.SourceLinkPath);
        File.CreateSymbolicLink(fixture.SourceLinkPath, "/legacy/changed");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(CanaryPathSafety.PathExistsNoFollow(Path.Join(fixture.LibraryRoot, "TV", "episode.mkv")));
    }

    [Fact]
    public async Task ApplyAsync_RejectsMissingSourceLink()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");
        File.Delete(fixture.SourceLinkPath);

        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(CanaryPathSafety.PathExistsNoFollow(Path.Join(fixture.LibraryRoot, "TV", "episode.mkv")));
    }

    [Fact]
    public async Task ApplyAsync_RevalidatesSourceImmediatelyBeforeCreate()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");
        var applier = new CanaryLinkApplier(_ => true, _ =>
        {
            File.Delete(fixture.SourceLinkPath);
            File.CreateSymbolicLink(fixture.SourceLinkPath, "/legacy/raced");
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(CanaryPathSafety.PathExistsNoFollow(Path.Join(fixture.LibraryRoot, "TV", "episode.mkv")));
    }

    [Fact]
    public async Task ApplyAsync_RevalidatesTargetSizeImmediatelyBeforeCreate()
    {
        var fixture = await CreateFixtureAsync("TV/episode.mkv");
        var applier = new CanaryLinkApplier(_ => true, path => File.WriteAllBytes(path, new byte[31]));

        await Assert.ThrowsAsync<InvalidDataException>(() => applier.ApplyAsync(
            fixture.PlanPath, fixture.SourceRoot, fixture.LibraryRoot, fixture.TargetRoot, fixture.JournalPath));

        Assert.False(CanaryPathSafety.PathExistsNoFollow(Path.Join(fixture.LibraryRoot, "TV", "episode.mkv")));
    }

    private async Task<Fixture> CreateFixtureAsync(string libraryPath, Action<string>? beforeCreate = null)
    {
        var library = Directory.CreateDirectory(Path.Join(_root, $"library-{Guid.NewGuid():N}")).FullName;
        var source = Directory.CreateDirectory(Path.Join(_root, $"source-{Guid.NewGuid():N}")).FullName;
        var target = Directory.CreateDirectory(Path.Join(_root, $"target-{Guid.NewGuid():N}")).FullName;
        var sourceRelativePath = Path.IsPathRooted(libraryPath)
                                 || libraryPath.Split('/').Any(component => component is "" or "." or "..")
            ? "fixture-source.mkv"
            : libraryPath;
        var sourcePath = Path.Join(source, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.CreateSymbolicLink(sourcePath, "/legacy/source");
        var relativeTarget = ".ids/a/b/c/d/e/11111111-1111-1111-1111-111111111111";
        var targetPath = Path.Join(target, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllBytesAsync(targetPath, new byte[32]);
        var plan = new NzbDavCanaryPlan(
            1, 7, new string('a', 64), DateTimeOffset.UtcNow, 1, 1, true,
            [new NzbDavCanaryPlanLink(
                libraryPath, "/legacy/source", Guid.NewGuid(), 32, "exact", "{}",
                relativeTarget, "planned")]);
        var planDir = await new NzbDavCanaryPlanWriter().WriteAsync(
            Path.Join(_root, $"plans-{Guid.NewGuid():N}"), plan);
        return new Fixture(
            Path.Join(planDir, "plan.json"), source, sourcePath, library, target,
            Path.Join(_root, $"journal-{Guid.NewGuid():N}.json"),
            new CanaryLinkApplier(_ => true, beforeCreate));
    }

    private sealed record Fixture(
        string PlanPath,
        string SourceRoot,
        string SourceLinkPath,
        string LibraryRoot,
        string TargetRoot,
        string JournalPath,
        CanaryLinkApplier Applier);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
