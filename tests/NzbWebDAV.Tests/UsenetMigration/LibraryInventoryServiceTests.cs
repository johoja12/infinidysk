using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbDavMigration.Recovery;

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
                HistoryDownloadStatus: 1, NzbSegmentsJson: "[\"with-history@test\"]"),
            new LegacyDavItemRow(withoutHistory, "/content/b.mkv", 1, 3, null, null,
                NzbSegmentsJson: "[\"orphan@test\"]", HistoryExclusion: "missing-history"),
            new LegacyDavItemRow(unresolvedBlob, "/content/d.mkv", 1, 3, Guid.NewGuid(), Guid.NewGuid(),
                NzbSegmentsJson: "[\"unresolved@test\"]"),
        };

        var result = new LibraryInventoryService().Enrich(links, rows, new LegacyBlobResolver(blobRoot));

        Assert.Equal("candidate", result.Single(item => item.LegacyDavItemId == withHistory).Status);
        Assert.Equal("recoverable-orphan", result.Single(item => item.LegacyDavItemId == withoutHistory).Status);
        Assert.Null(result.Single(item => item.LegacyDavItemId == withoutHistory).ExclusionReason);
        Assert.Equal("missing-database-row", result.Single(item => item.LegacyDavItemId == missingRow).ExclusionReason);
        Assert.Equal("missing-nzb-blob", result.Single(item => item.LegacyDavItemId == unresolvedBlob).ExclusionReason);
    }

    [Fact]
    public void Enrich_PreservesOrphanRarAndMultipartMetadataButRejectsSafetyFailures()
    {
        Directory.CreateDirectory(_root);
        var direct = Guid.NewGuid();
        var rar = Guid.NewGuid();
        var multipart = Guid.NewGuid();
        var unsafeItem = Guid.NewGuid();
        var links = new[]
        {
            new LibraryInventoryLink("direct.mkv", "/.ids/direct", direct),
            new LibraryInventoryLink("rar.mkv", "/.ids/rar", rar),
            new LibraryInventoryLink("multipart.mkv", "/.ids/multipart", multipart),
            new LibraryInventoryLink("unsafe.mkv", "/.ids/unsafe", unsafeItem),
        };
        var rows = new[]
        {
            new LegacyDavItemRow(direct, "/content/direct.mkv", 1, 3, null, null,
                NzbSegmentsJson: "[\"direct@test\"]", HistoryExclusion: "missing-history"),
            new LegacyDavItemRow(rar, "/content/rar.mkv", 2, 4, null, null,
                RarPartsJson: "[{\"SegmentIds\":[\"rar@test\"]}]", HistoryExclusion: "missing-history"),
            new LegacyDavItemRow(multipart, "/content/multipart.mkv", 3, 6, null, null,
                MultipartMetadataJson: "{\"FileParts\":[{\"SegmentIds\":[\"multipart@test\"]}]}",
                HistoryExclusion: "missing-history"),
            new LegacyDavItemRow(unsafeItem, "/content/unsafe.mkv", 4, 3, null, null,
                NzbSegmentsJson: "[\"unsafe@test\"]", HistoryExclusion: "missing-history",
                SafetyExclusion: "invalid-ancestry", ResolutionExclusion: "invalid-ancestry"),
        };

        var result = new LibraryInventoryService().Enrich(
            links, rows, new LegacyBlobResolver(Path.Join(_root, "blobs")));

        Assert.All(result.Where(item => item.LegacyDavItemId != unsafeItem), item =>
            Assert.Equal("recoverable-orphan", item.Status));
        var excluded = result.Single(item => item.LegacyDavItemId == unsafeItem);
        Assert.Equal("excluded", excluded.Status);
        Assert.Equal("invalid-ancestry", excluded.ExclusionReason);
    }

    [Fact]
    public void FullRecoveryInventory_RecordsVersionedSnapshotAndTerminalClasses()
    {
        var inventory = new FullRecoveryInventory(
            FullRecoveryInventory.CurrentSchemaVersion,
            new FullRecoverySnapshotDigests(new string('a', 64), new string('b', 64)),
            DateTimeOffset.UtcNow,
            [new FullRecoveryInventoryItem("TV/show.mkv", Guid.NewGuid(), "recoverable-orphan", null)]);

        Assert.Equal(1, inventory.SchemaVersion);
        Assert.Equal("recoverable-orphan", Assert.Single(inventory.Items).TerminalClassification);
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
