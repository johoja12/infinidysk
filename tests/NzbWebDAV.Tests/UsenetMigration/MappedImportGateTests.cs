using System.Text.Json;
using NzbDavMigration.Inventory;
using NzbDavMigration.Recovery;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class MappedImportGateTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"mapped-gates-{Guid.NewGuid():N}");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    public async Task CrossRootGate_BlocksSharedNzbPayloadBeforeExport(bool sharedPayload, int expectedExit)
    {
        Directory.CreateDirectory(_root);
        var plex = Snapshot("/mnt/plex", Guid.NewGuid());
        var special = Snapshot("/mnt/special", Guid.NewGuid());
        var plexMaster = Master(plex, new string('a', 64));
        var specialMaster = Master(special, new string(sharedPayload ? 'a' : 'b', 64));
        await WriteAsync("plex-inventory.json", plex);
        await WriteAsync("special-inventory.json", special);
        await WriteAsync("plex-master.json", plexMaster);
        await WriteAsync("special-master.json", specialMaster);

        var exit = await NzbDavMigrationProgram.RunAsync(
        [
            "verify-mapped-roots",
            "--plex-inventory", Path.Join(_root, "plex-inventory.json"),
            "--special-inventory", Path.Join(_root, "special-inventory.json"),
            "--plex-master", Path.Join(_root, "plex-master.json"),
            "--special-master", Path.Join(_root, "special-master.json"),
        ]);

        Assert.Equal(expectedExit, exit);
    }

    [Fact]
    public async Task FullRecovery_RefusesLoweredMappedCoverageGate()
    {
        Directory.CreateDirectory(_root);
        await WriteAsync("plex-inventory.json", Snapshot("/mnt/plex", Guid.NewGuid()));

        var exit = await NzbDavMigrationProgram.RunAsync(
        [
            "recover-full", "--inventory", Path.Join(_root, "plex-inventory.json"),
            "--catalogue", Path.Join(_root, "missing-catalogue.sqlite"),
            "--output", Path.Join(_root, "recovery"),
            "--minimum-coverage", "0.50",
        ]);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Join(_root, "recovery")));
    }

    private static MappedLibraryInventory Snapshot(string source, Guid id)
    {
        var target = $"/mnt/remote/nzbdav/.ids/a/b/c/{id}";
        var rows = new[]
        {
            new MappedLibraryInventoryRow(Path.Join(source, "one.mkv"), id, false,
                new LegacyInventoryCandidate("one.mkv", target, id, null, "recoverable-orphan", null, null)),
        };
        return new MappedLibraryInventory(1, source, "/mnt/remote/nzbdav/.ids", DateTimeOffset.UtcNow,
            0, MappedLibraryInventory.ComputeRowsSha256(rows), rows);
    }

    private static FullRecoveryMasterManifest Master(MappedLibraryInventory snapshot, string payload) =>
        new(1, DateTimeOffset.UtcNow, 1, 1, 1m,
        [new LegacySourceRecoveryItem("one.mkv", snapshot.Rows[0].Candidate.OriginalTarget,
            snapshot.Rows[0].DavItemId, "/content/one.mkv", "exact-direct", null,
            "blob.nzb", payload, "direct-articles-v1", new string('c', 64), 123)],
        new MappedSourceProof(snapshot.SourceRoot, snapshot.LegacyIdsRoot, 1, snapshot.RowsSha256));

    private async Task WriteAsync<T>(string name, T value) =>
        await File.WriteAllTextAsync(Path.Join(_root, name), JsonSerializer.Serialize(value, JsonOptions));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
