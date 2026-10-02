using NzbWebDAV.Services.Regrab;

namespace NzbWebDAV.Tests.Services.Regrab;

public sealed class LibrarySymlinkGuardTests : IDisposable
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("regrab-guard-").FullName;
    private readonly string _root;
    private readonly string _outside;

    public LibrarySymlinkGuardTests()
    {
        _root = Path.Join(_sandbox, "library");
        _outside = Path.Join(_sandbox, "outside");
        Directory.CreateDirectory(Path.Join(_root, "TV", "Show", "Season 1"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private IReadOnlyList<string> Roots => LibrarySymlinkGuard.NormalizeRoots([_root + "/"]);

    private string Target(string name)
    {
        var path = Path.Join(_outside, name);
        File.WriteAllText(path, "payload");
        return path;
    }

    [Fact]
    public void RemoveSymlink_RemovesOnlyTheLinkAndKeepsItsTarget()
    {
        var target = Target("episode.mkv");
        var link = Path.Join(_root, "TV", "Show", "Season 1", "episode.mkv");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(LibraryLinkInspection.Symlink, LibrarySymlinkGuard.Inspect(link, Roots).Kind);
        var removedTarget = LibrarySymlinkGuard.RemoveSymlink(link, Roots, expectedTarget: target, expectedLegacyId: null);

        Assert.Equal(target, removedTarget);
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(link));
        Assert.Equal("payload", File.ReadAllText(target));
    }

    [Fact]
    public void RemoveSymlink_RefusesRegularFiles()
    {
        var file = Path.Join(_root, "TV", "Show", "Season 1", "real.mkv");
        File.WriteAllText(file, "real media");

        Assert.Equal(LibraryLinkInspection.NotSymlink, LibrarySymlinkGuard.Inspect(file, Roots).Kind);
        Assert.Throws<InvalidOperationException>(() => LibrarySymlinkGuard.RemoveSymlink(file, Roots, null, null));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Inspect_RefusesPathsOutsideConfiguredRoots()
    {
        var link = Path.Join(_outside, "elsewhere.mkv");
        File.CreateSymbolicLink(link, Target("x.mkv"));

        Assert.Equal(LibraryLinkInspection.OutsideRoots, LibrarySymlinkGuard.Inspect(link, Roots).Kind);
        Assert.Throws<InvalidOperationException>(() => LibrarySymlinkGuard.RemoveSymlink(link, Roots, null, null));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Inspect_RefusesLinksReachedThroughSymlinkedDirectories()
    {
        var realDir = Path.Join(_outside, "real-season");
        Directory.CreateDirectory(realDir);
        var link = Path.Join(realDir, "episode.mkv");
        File.CreateSymbolicLink(link, Target("y.mkv"));
        Directory.CreateSymbolicLink(Path.Join(_root, "TV", "Linked"), realDir);

        var throughLink = Path.Join(_root, "TV", "Linked", "episode.mkv");
        Assert.Equal(LibraryLinkInspection.SymlinkedParent, LibrarySymlinkGuard.Inspect(throughLink, Roots).Kind);
        Assert.Throws<InvalidOperationException>(() => LibrarySymlinkGuard.RemoveSymlink(throughLink, Roots, null, null));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Inspect_RefusesNonNormalizedAndRelativePaths()
    {
        Assert.Equal(LibraryLinkInspection.InvalidPath,
            LibrarySymlinkGuard.Inspect(Path.Join(_root, "TV", "..", "TV", "x.mkv"), Roots).Kind);
        Assert.Equal(LibraryLinkInspection.InvalidPath,
            LibrarySymlinkGuard.Inspect("TV/x.mkv", Roots).Kind);
    }

    [Fact]
    public void Inspect_ReportsMissingAndChangedLegacyTargets()
    {
        var legacyId = Guid.NewGuid();
        var link = Path.Join(_root, "TV", "Show", "Season 1", "legacy.mkv");
        Assert.Equal(LibraryLinkInspection.Missing, LibrarySymlinkGuard.Inspect(link, Roots).Kind);

        File.CreateSymbolicLink(link, $"/mnt/remote/nzbdav/.ids/1/2/3/4/5/{Guid.NewGuid()}");
        Assert.Equal(LibraryLinkInspection.TargetMismatch,
            LibrarySymlinkGuard.Inspect(link, Roots, expectedLegacyId: legacyId).Kind);

        File.Delete(link);
        File.CreateSymbolicLink(link, $"/mnt/remote/nzbdav/.ids/1/2/3/4/5/{legacyId}");
        Assert.Equal(LibraryLinkInspection.Symlink,
            LibrarySymlinkGuard.Inspect(link, Roots, expectedLegacyId: legacyId).Kind);
        Assert.Equal(LibraryLinkInspection.TargetMismatch,
            LibrarySymlinkGuard.Inspect(link, Roots, expectedTarget: "/somewhere/else").Kind);
    }

    [Theory]
    [InlineData("/mnt/remote/nzbdav/.ids/2/a/6/d/d/2a6dda43-4838-fe5b-a57e-a4d85e2ad1d7", true)]
    [InlineData("/mnt/remote/nzbdav/.ids/2/a/6/d/d/2a6dda43-4838-fe5b-a57e-a4d85e2ad1d7.mkv", true)]
    [InlineData("/mnt/remote/nzbdav/content/2a6dda43-4838-fe5b-a57e-a4d85e2ad1d7", false)]
    [InlineData("/mnt/remote/nzbdav/.ids/1/1/1/1/1/11111111-1111-1111-1111-111111111111", false)]
    public void TargetsDavItem_MatchesOnlyIdsTargetsForTheItem(string target, bool expected)
    {
        Assert.Equal(expected,
            LibrarySymlinkGuard.TargetsDavItem(target, Guid.Parse("2a6dda43-4838-fe5b-a57e-a4d85e2ad1d7")));
    }
}
