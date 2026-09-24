using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbDavMigration.Canary;
using NzbDavMigration.Recovery;
using System.Text.Json;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class MappedLibraryInventoryTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"mapped-inventory-{Guid.NewGuid():N}");

    [Fact]
    public void Build_KeepsEveryDatabaseMappingInDenominatorAndExcludesInvalidSources()
    {
        var source = Path.Join(_root, "plex");
        var ids = Path.Join(_root, "legacy", ".ids");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(ids);
        var valid = Guid.NewGuid();
        var broken = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var wrongId = Guid.NewGuid();
        var wrongRoot = Guid.NewGuid();
        var unmapped = Guid.NewGuid();
        CreateLink(source, "valid.mkv", Path.Join(ids, "1", "2", valid.ToString()));
        CreateLink(source, "broken.mkv", Path.Join(ids, broken.ToString()));
        CreateLink(source, "wrong-id.mkv", Path.Join(ids, Guid.NewGuid().ToString()));
        CreateLink(source, "wrong-root.mkv", Path.Join(_root, "elsewhere", ".ids", wrongRoot.ToString()));
        CreateLink(source, "unmapped.mkv", Path.Join(ids, unmapped.ToString()));
        var database = new LegacyMappedReadResult(
        [
            Row("valid.mkv", valid),
            Row("broken.mkv", broken, isBroken: true),
            Row("missing.mkv", missing),
            Row("wrong-id.mkv", wrongId),
            Row("wrong-root.mkv", wrongRoot),
        ],
        [new LegacyDavItemRow(valid, "/content/valid.mkv", 123, 3, null, null,
            NzbSegmentsJson: "[\"article@test\"]", HistoryExclusion: "missing-history")], []);

        var result = new MappedLibraryInventoryBuilder().Build(
            source, ids, database, new LibraryInventoryService().Inventory(source), Path.Join(_root, "blobs"));

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(1, result.OutOfScopeFilesystemLinks);
        Assert.Equal("recoverable-orphan", result.Rows.Single(row =>
            row.Candidate.LibraryRelativePath == "valid.mkv").Candidate.Status);
        Assert.Contains(result.Rows, row => row.Candidate.ExclusionReason == "broken-mapping");
        Assert.Contains(result.Rows, row => row.Candidate.ExclusionReason == "missing-source-link");
        Assert.Contains(result.Rows, row => row.Candidate.ExclusionReason == "mapping-target-id-mismatch");
        Assert.Contains(result.Rows, row => row.Candidate.ExclusionReason == "mapping-target-root-mismatch");
        result.Validate();
        Assert.Throws<InvalidDataException>(() => (result with { RowsSha256 = new string('0', 64) }).Validate());
    }

    [Fact]
    public async Task MappedCoverage_CountsDatabaseRowsWhoseSourceSymlinkIsMissing()
    {
        var source = Directory.CreateDirectory(Path.Join(_root, "plex")).FullName;
        var library = Directory.CreateDirectory(Path.Join(_root, "plex2")).FullName;
        var ids = Directory.CreateDirectory(Path.Join(_root, "legacy", ".ids")).FullName;
        var journals = Directory.CreateDirectory(Path.Join(_root, "journals")).FullName;
        var validId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var valid = new MappedLibraryInventoryRow(Path.Join(source, "valid.mkv"), validId, false,
            new LegacyInventoryCandidate("valid.mkv", Path.Join(ids, validId.ToString()), validId,
                null, "candidate", null, null));
        var missing = new MappedLibraryInventoryRow(Path.Join(source, "missing.mkv"), missingId, false,
            new LegacyInventoryCandidate("missing.mkv", string.Empty, missingId,
                null, "excluded", "missing-source-link", null));
        var initialRows = new[] { valid };
        var finalRows = new[] { valid, missing };
        var initial = new MappedLibraryInventory(1, source, ids, DateTimeOffset.UtcNow, 0,
            MappedLibraryInventory.ComputeRowsSha256(initialRows), initialRows);
        var final = new MappedLibraryInventory(1, source, ids, DateTimeOffset.UtcNow, 0,
            MappedLibraryInventory.ComputeRowsSha256(finalRows), finalRows);
        var master = new FullRecoveryMasterManifest(1, DateTimeOffset.UtcNow, 1, 1, 1m,
        [new LegacySourceRecoveryItem("valid.mkv", valid.Candidate.OriginalTarget, validId,
            "/content/valid.mkv", "exact-direct", null, "blob.nzb", new string('a', 64),
            "direct-articles-v1", new string('b', 64), 123)],
            new MappedSourceProof(source, ids, 1, initial.RowsSha256));
        var masterPath = Path.Join(_root, "master.json");
        await File.WriteAllTextAsync(masterPath, JsonSerializer.Serialize(master));

        var result = await new CanaryCoverageReporter().WriteAsync(source, library,
            Path.Join(_root, "unused-initial.json"), masterPath, journals, Path.Join(_root, "coverage"),
            initialMapped: initial, finalMapped: final);

        Assert.Equal(2, result.Report.FinalSourceCount);
        Assert.Equal(0, result.Report.CoveredCount);
        Assert.Equal("missing-source-link", result.Report.Items.Single(item =>
            item.LibraryRelativePath == "missing.mkv").Classification);
        Assert.False(result.Report.MeetsMinimumCoverage);
    }

    [Fact]
    public void Build_ExcludesDuplicateDavItemMappingsWithoutDroppingEitherRow()
    {
        var source = Directory.CreateDirectory(Path.Join(_root, "plex")).FullName;
        var ids = Directory.CreateDirectory(Path.Join(_root, "legacy", ".ids")).FullName;
        var id = Guid.NewGuid();
        CreateLink(source, "one.mkv", Path.Join(ids, id.ToString()));
        CreateLink(source, "two.mkv", Path.Join(ids, id.ToString()));
        var database = new LegacyMappedReadResult(
            [Row("one.mkv", id), Row("two.mkv", id)], [], []);

        var snapshot = new MappedLibraryInventoryBuilder().Build(
            source, ids, database, new LibraryInventoryService().Inventory(source), Path.Join(_root, "blobs"));

        Assert.Equal(2, snapshot.Rows.Count);
        Assert.All(snapshot.Rows, row => Assert.Equal("duplicate-mapped-id", row.Candidate.ExclusionReason));
    }

    private LegacyLocalLinkRow Row(string relative, Guid id, bool isBroken = false) =>
        new(Path.Join(_root, "plex", relative), id, isBroken);

    private static void CreateLink(string source, string name, string target) =>
        File.CreateSymbolicLink(Path.Join(source, name), target);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
