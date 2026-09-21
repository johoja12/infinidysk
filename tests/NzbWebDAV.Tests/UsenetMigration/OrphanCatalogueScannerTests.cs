using NzbDavMigration.Catalogue;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class OrphanCatalogueScannerTests : IAsyncDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"orphan-scan-{Guid.NewGuid():N}");

    [Fact]
    public async Task Scan_CataloguesRegularStandardDoctypeMalformedAndSymlinkInputs()
    {
        var blobRoot = CreateBlobRoot();
        await File.WriteAllTextAsync(Path.Join(blobRoot, "regular.nzb"), Nzb("regular@test"));
        await File.WriteAllTextAsync(Path.Join(blobRoot, "doctype.nzb"), StandardDoctypeNzb("doctype@test"));
        await File.WriteAllTextAsync(Path.Join(blobRoot, "entity.nzb"),
            "<!DOCTYPE nzb [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><nzb>&xxe;</nzb>");
        await File.WriteAllTextAsync(Path.Join(blobRoot, "malformed.nzb"), "<nzb>");
        File.CreateSymbolicLink(Path.Join(blobRoot, "linked.nzb"), Path.Join(blobRoot, "regular.nzb"));
        var inventory = await FreezeAsync(blobRoot);
        var (database, summary) = OutputPaths();

        await using var store = new OrphanCatalogueStore(database, summary);
        var result = await new OrphanCatalogueScanner().ScanAsync(blobRoot, inventory, store);

        Assert.Equal(5, result.BlobCount);
        Assert.Equal(2, result.ValidBlobCount);
        Assert.Equal(3, result.FailedBlobCount);
        Assert.Equal("valid", (await store.ReadBlobAsync("regular.nzb"))!.ParseStatus);
        Assert.Equal("invalid-xml", (await store.ReadBlobAsync("entity.nzb"))!.FailureClass);
        Assert.Equal("invalid-xml", (await store.ReadBlobAsync("malformed.nzb"))!.FailureClass);
        Assert.Equal("symlink", (await store.ReadBlobAsync("linked.nzb"))!.FailureClass);
    }

    [Fact]
    public async Task Scan_ClassifiesChangedDuringReadAndGroupsDuplicateBytes()
    {
        var blobRoot = CreateBlobRoot();
        var duplicate = Nzb("duplicate@test");
        await File.WriteAllTextAsync(Path.Join(blobRoot, "one.nzb"), duplicate);
        await File.WriteAllTextAsync(Path.Join(blobRoot, "two.nzb"), duplicate);
        await File.WriteAllTextAsync(Path.Join(blobRoot, "changed.nzb"), Nzb("changed@test"));
        var inventory = await FreezeAsync(blobRoot);
        var changed = false;
        var scanner = new OrphanCatalogueScanner(afterRead: async (path, _) =>
        {
            if (!changed && path.EndsWith("changed.nzb", StringComparison.Ordinal))
            {
                changed = true;
                await File.AppendAllTextAsync(path, " ");
            }
        });
        var (database, summary) = OutputPaths();

        await using var store = new OrphanCatalogueStore(database, summary);
        await scanner.ScanAsync(blobRoot, inventory, store);

        var one = await store.ReadBlobAsync("one.nzb");
        var two = await store.ReadBlobAsync("two.nzb");
        Assert.Equal(one!.Sha256, two!.Sha256);
        Assert.Equal(["one.nzb", "two.nzb"], await store.FindBlobPathsBySha256Async(one.Sha256!));
        Assert.Equal("changed", (await store.ReadBlobAsync("changed.nzb"))!.FailureClass);
    }

    [Fact]
    public async Task Scan_RejectsAParentDirectoryReplacedByASymlinkAfterFreeze()
    {
        var blobRoot = CreateBlobRoot();
        var shard = Path.Join(blobRoot, "aa");
        Directory.CreateDirectory(shard);
        await File.WriteAllTextAsync(Path.Join(shard, "one.nzb"), Nzb("one@test"));
        var inventory = await FreezeAsync(blobRoot);
        var moved = Path.Join(_root, "moved-shard");
        Directory.Move(shard, moved);
        Directory.CreateSymbolicLink(shard, moved);
        var (database, summary) = OutputPaths();

        await using var store = new OrphanCatalogueStore(database, summary);
        await new OrphanCatalogueScanner().ScanAsync(blobRoot, inventory, store);

        Assert.Equal("symlink", (await store.ReadBlobAsync("aa/one.nzb"))!.FailureClass);
    }

    [Fact]
    public async Task Scan_CancellationLeavesBuildingStateAndResumeSkipsCompletedSnapshots()
    {
        var blobRoot = CreateBlobRoot();
        await File.WriteAllTextAsync(Path.Join(blobRoot, "one.nzb"), Nzb("one@test"));
        await File.WriteAllTextAsync(Path.Join(blobRoot, "two.nzb"), Nzb("two@test"));
        var inventory = await FreezeAsync(blobRoot);
        var (database, summary) = OutputPaths();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var interrupted = new OrphanCatalogueScanner(afterCommit: (_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1) cancellation.Cancel();
            return Task.CompletedTask;
        });
        await using (var store = new OrphanCatalogueStore(database, summary))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                interrupted.ScanAsync(blobRoot, inventory, store, cancellation.Token));
            Assert.Equal("building", (await store.ReadStateAsync()).Status);
        }

        var resumedReads = 0;
        await using var resumed = new OrphanCatalogueStore(database, summary);
        var result = await new OrphanCatalogueScanner(afterRead: (_, _) =>
        {
            Interlocked.Increment(ref resumedReads);
            return Task.CompletedTask;
        }).ScanAsync(blobRoot, inventory, resumed);
        Assert.Equal(2, result.ValidBlobCount);
        Assert.Equal(1, resumedReads);
        Assert.Equal("complete", (await resumed.ReadStateAsync()).Status);
    }

    [Fact]
    public async Task FrozenListAndScanCli_CreatePrivateOutputs()
    {
        if (OperatingSystem.IsWindows()) return;
        var blobRoot = CreateBlobRoot();
        await File.WriteAllTextAsync(Path.Join(blobRoot, "one.nzb"), Nzb("one@test"));
        var inventory = Path.Join(_root, "inventory.json");
        var (database, summary) = OutputPaths();

        Assert.Equal(0, await NzbDavMigrationProgram.RunAsync(
            ["catalogue-list", "--blob-root", blobRoot, "--output", inventory]));
        Assert.Equal(0, await NzbDavMigrationProgram.RunAsync(
            ["catalogue-scan", "--blob-root", blobRoot, "--inventory", inventory,
                "--database", database, "--summary", summary]));

        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(inventory));
        Assert.Equal(expected, File.GetUnixFileMode(database));
        Assert.Equal(expected, File.GetUnixFileMode(summary));
    }

    private string CreateBlobRoot()
    {
        var path = Path.Join(_root, "blobs");
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<string> FreezeAsync(string blobRoot)
    {
        var path = Path.Join(_root, "inventory.json");
        await OrphanCatalogueInputList.CreateAsync(blobRoot, path);
        return path;
    }

    private (string Database, string Summary) OutputPaths() =>
        (Path.Join(_root, "catalogue.sqlite"), Path.Join(_root, "summary.json"));

    private static string Nzb(string messageId) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file poster="test" date="1" subject="safe">
            <groups><group>alt.test</group></groups>
            <segments><segment bytes="123" number="1">{{messageId}}</segment></segments>
          </file>
        </nzb>
        """;

    private static string StandardDoctypeNzb(string messageId) => Nzb(messageId).Replace(
        "?>",
        "?>\n<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\">",
        StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        await ValueTask.CompletedTask;
    }
}
