using System.Text.Json;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbDavMigration.Recovery;
using NzbWebDAV.Database.Models;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class LegacySourceRecoveryTests : IAsyncDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"legacy-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task Recover_ClassifiesDirectArchiveLogicalDuplicatesAmbiguityAndMissingEvidence()
    {
        await using var store = await BuildCatalogueAsync();
        var rows = BuildInventory();

        var report = await new LegacySourceRecovery().RecoverAsync(rows, store);

        Assert.Equal("exact-direct", report.Items[0].Classification);
        Assert.Equal("exact-archive", report.Items[1].Classification);
        Assert.Equal("ambiguous-payload", report.Items[2].Classification);
        Assert.Equal("missing-articles", report.Items[3].Classification);
        Assert.Equal(4, report.TotalLinks);
        Assert.Equal(2, report.RecoverableLinks);
        Assert.Equal(0.5m, report.RecoverableFraction);
        Assert.Equal("duplicate-a.nzb", report.Items[0].SourceRelativePath);
    }

    [Fact]
    public async Task Recover_DoesNotUseNamesAndWritesCompleteChecksummedReportsBelowGate()
    {
        await using var store = await BuildCatalogueAsync();
        var rows = BuildInventory();
        var nameOnly = Candidate("name-only/movie.mkv", new LegacyDavItemRow(
            Guid.NewGuid(), "/content/payload-name-matches-movie.mkv", 100, 3, null, null));
        var output = Path.Join(_root, "recovery-output");

        var result = await new LegacySourceRecovery().WriteAsync([.. rows, nameOnly], store, output, 0.90m);

        Assert.False(result.MeetsMinimumCoverage);
        Assert.Equal("missing-articles", result.Report.Items[^1].Classification);
        Assert.True(File.Exists(Path.Join(output, "recovery.json")));
        Assert.True(File.Exists(Path.Join(output, "master-manifest.json")));
        Assert.True(File.Exists(Path.Join(output, "exclusions.json")));
        Assert.True(File.Exists(Path.Join(output, "SHA256SUMS")));
        Assert.False(File.Exists(Path.Join(output, "batch-assignments.json")));
        var master = JsonSerializer.Deserialize<FullRecoveryMasterManifest>(
            await File.ReadAllTextAsync(Path.Join(output, "master-manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(5, master.Items.Count);
    }

    private async Task<OrphanCatalogueStore> BuildCatalogueAsync()
    {
        Directory.CreateDirectory(_root);
        var store = new OrphanCatalogueStore(
            Path.Join(_root, $"catalogue-{Guid.NewGuid():N}.sqlite"),
            Path.Join(_root, $"summary-{Guid.NewGuid():N}.json"));
        var inputDigest = Digest('f');
        await store.BeginAsync(inputDigest);
        await store.UpsertAsync(Blob("duplicate-a.nzb", Digest('1'),
            [("a@test", 1, 100L), ("b@test", 2, 200L)]));
        await store.UpsertAsync(Blob("duplicate-b.nzb", Digest('1'),
            [("a@test", 1, 100L), ("b@test", 2, 200L)]));
        await store.UpsertAsync(Blob("archive.nzb", Digest('2'), [("arc@test", 1, 300L)]));
        await store.UpsertAsync(Blob("ambiguous-a.nzb", Digest('3'), [("x@test", 1, 100L)]));
        await store.UpsertAsync(Blob("ambiguous-b.nzb", Digest('4'), [("x@test", 1, 100L)]));
        var summary = await store.BuildSummaryAsync(inputDigest);
        await store.SealAsync(summary);
        return store;
    }

    private static LegacyInventoryCandidate[] BuildInventory()
    {
        var rar = JsonSerializer.Serialize(new[]
        {
            new DavRarFile.RarPart
            {
                SegmentIds = ["arc@test"], PartSize = 300, Offset = 10, ByteCount = 50,
            },
        });
        return
        [
            Candidate("tv/direct.mkv", new LegacyDavItemRow(Guid.NewGuid(), "/content/direct.mkv", 300, 3,
                null, null, NzbSegmentsJson: "[\"a@test\",\"b@test\"]")),
            Candidate("tv/archive.mkv", new LegacyDavItemRow(Guid.NewGuid(), "/content/archive.mkv", 50, 4,
                null, null, RarPartsJson: rar)),
            Candidate("tv/ambiguous.mkv", new LegacyDavItemRow(Guid.NewGuid(), "/content/ambiguous.mkv", 100, 3,
                null, null, NzbSegmentsJson: "[\"x@test\"]")),
            Candidate("tv/missing.mkv", new LegacyDavItemRow(Guid.NewGuid(), "/content/missing.mkv", 100, 3,
                null, null, NzbSegmentsJson: "[\"missing@test\"]")),
        ];
    }

    private static LegacyInventoryCandidate Candidate(string path, LegacyDavItemRow row) =>
        new(path, $"/.ids/{row.Id}", row.Id, row, "recoverable-orphan", null, null);

    private static OrphanCatalogueBlob Blob(
        string path,
        string sha256,
        IReadOnlyList<(string Id, int Number, long Bytes)> segments)
    {
        var identitySegments = segments.Select(segment =>
            new NzbDavArticleSegment(segment.Number, segment.Bytes, segment.Id)).ToArray();
        return new OrphanCatalogueBlob(path, 100, 200, sha256, "valid", null,
            NzbDavArticleIdentity.ComputeRelease([identitySegments]),
            segments.Select(segment => new OrphanCatalogueArticle(
                segment.Id, 0, segment.Number, segment.Bytes)).ToArray());
    }

    private static string Digest(char value) => new(value, 64);

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        await ValueTask.CompletedTask;
    }
}
