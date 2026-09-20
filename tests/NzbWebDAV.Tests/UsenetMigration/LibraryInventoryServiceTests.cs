using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class LibraryInventoryServiceTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-inventory-{Guid.NewGuid():N}");

    [Fact]
    public void Inventory_FindsOnlyLegacyIdSymlinksWithoutFollowingDirectoryLinks()
    {
        Directory.CreateDirectory(Path.Join(_root, "TV", "Show"));
        var selectedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        File.CreateSymbolicLink(
            Path.Join(_root, "TV", "Show", "episode.mkv"),
            $"/mnt/remote/nzbdav/.ids/1/1/1/1/1/{selectedId}");
        File.CreateSymbolicLink(Path.Join(_root, "TV", "ordinary-link"), "/tmp/not-a-legacy-id");

        var outside = Path.Join(Path.GetTempPath(), $"nzbdav-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(
            Path.Join(outside, "hidden.mkv"),
            "/mnt/remote/nzbdav/.ids/2/2/2/2/2/22222222-2222-2222-2222-222222222222");
        Directory.CreateSymbolicLink(Path.Join(_root, "linked-directory"), outside);

        try
        {
            var result = new LibraryInventoryService().Inventory(_root);

            var item = Assert.Single(result);
            Assert.Equal("TV/Show/episode.mkv", item.LibraryRelativePath);
            Assert.Equal(selectedId, item.LegacyDavItemId);
            Assert.StartsWith("/mnt/remote/nzbdav/.ids/", item.OriginalTarget, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Enrich_AccountsForHistoryMissingRowsAndUnresolvedBlobs()
    {
        Directory.CreateDirectory(_root);
        var blobRoot = Path.Join(_root, "blobs");
        var withHistory = Guid.NewGuid();
        var withoutHistory = Guid.NewGuid();
        var missingRow = Guid.NewGuid();
        var unresolvedBlob = Guid.NewGuid();
        var availableBlob = Guid.NewGuid();
        WriteBlob(blobRoot, availableBlob);
        var links = new[]
        {
            new LibraryInventoryLink("a.mkv", "/.ids/a", withHistory),
            new LibraryInventoryLink("b.mkv", "/.ids/b", withoutHistory),
            new LibraryInventoryLink("c.mkv", "/.ids/c", missingRow),
            new LibraryInventoryLink("d.mkv", "/.ids/d", unresolvedBlob),
        };
        var rows = new[]
        {
            new LegacyDavItemRow(withHistory, "/content/a.mkv", 1, 3, Guid.NewGuid(), availableBlob,
                HistoryDownloadStatus: 1),
            new LegacyDavItemRow(withoutHistory, "/content/b.mkv", 1, 3, null, availableBlob),
            new LegacyDavItemRow(unresolvedBlob, "/content/d.mkv", 1, 3, Guid.NewGuid(), Guid.NewGuid()),
        };

        var result = new LibraryInventoryService().Enrich(links, rows, new LegacyBlobResolver(blobRoot));

        Assert.Equal("candidate", result.Single(item => item.LegacyDavItemId == withHistory).Status);
        Assert.Equal("missing-history", result.Single(item => item.LegacyDavItemId == withoutHistory).ExclusionReason);
        Assert.Equal("missing-database-row", result.Single(item => item.LegacyDavItemId == missingRow).ExclusionReason);
        Assert.Equal("missing-nzb-blob", result.Single(item => item.LegacyDavItemId == unresolvedBlob).ExclusionReason);
    }

    private static void WriteBlob(string root, Guid id)
    {
        var compact = id.ToString("N");
        var path = Path.Join(root, compact[..2], compact.Substring(2, 2), id.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<nzb />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
