using System.Text.Json;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class LegacyCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"legacy-compat-{Guid.NewGuid():N}");

    [Fact]
    public async Task Export_CamelCaseInventoryRetainedInlineNzbPreservesExactBytesAndSizes()
    {
        Directory.CreateDirectory(_root);
        var nzb = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory,
            "Fixtures", "UsenetMigration", "direct-sample.nzb"));
        var history = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 20).Select(index => new
        {
            LibraryRelativePath = $"episode-{index}.mkv", OriginalTarget = $"/.ids/{index}",
            LegacyDavItemId = Guid.NewGuid(), Status = "candidate", ExclusionReason = (string?)null,
            ResolvedNzbPath = (string?)null,
        }).Select(candidate => new
        {
            candidate.LibraryRelativePath, candidate.OriginalTarget, candidate.LegacyDavItemId,
            candidate.Status, candidate.ExclusionReason, candidate.ResolvedNzbPath,
            Item = new
            {
                Id = candidate.LegacyDavItemId, Path = "/content/release/Canary.Direct.mkv",
                FileSize = 999L, Type = 3, HistoryItemId = history, NzbBlobId = history,
                HistoryDownloadStatus = 1, NzbSegmentsJson = "[\"direct-1@test\",\"direct-2@test\"]",
                NzbContents = nzb, ReleaseRootPath = "/content/release",
            },
        }).ToArray();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var inventory = Path.Join(_root, "inventory.json");
        var selection = Path.Join(_root, "selection.json");
        await File.WriteAllTextAsync(inventory, JsonSerializer.Serialize(candidates, options));
        await File.WriteAllTextAsync(selection, JsonSerializer.Serialize(new SelectionDocument(
            candidates.Select(c => new SelectionEntry(c.LibraryRelativePath, c.LegacyDavItemId)).ToArray())));
        var output = Path.Join(_root, "package");

        var result = await NzbDavMigrationProgram.RunAsync(["export", "--inventory", inventory,
            "--selection", selection, "--blob-root", Path.Join(_root, "blobs"), "--output", output,
            "--package-id", "inline-test"]);

        Assert.Equal(0, result);
        Assert.Equal(nzb, await File.ReadAllTextAsync(Path.Join(output, "payloads", $"{history}.nzb")));
        var manifest = NzbDavExportManifestJson.Deserialize(await File.ReadAllTextAsync(Path.Join(output, "manifest.json")));
        Assert.All(manifest.Releases.SelectMany(r => r.Leaves), leaf => Assert.Equal(999, leaf.FileSize));
    }

    [Fact]
    public async Task DirectIdentity_RejectsDuplicateAndUnmatchedArticlesWithoutEchoingMetadata()
    {
        await using var stream = File.OpenRead(Path.Join(AppContext.BaseDirectory,
            "Fixtures", "UsenetMigration", "direct-sample.nzb"));
        var document = await NzbWebDAV.Models.Nzb.NzbDocument.LoadAsync(stream);
        var row = new LegacyDavItemRow(Guid.NewGuid(), "/content/a.mkv", 99, 3, Guid.NewGuid(), Guid.NewGuid(),
            NzbSegmentsJson: "[\"secret-article@test\"]");
        var unmatched = new LegacyIdentityExtractor().Extract(row, document, "release");
        Assert.Equal("excluded", unmatched.ExtractionStatus);
        Assert.DoesNotContain("secret-article", unmatched.ExclusionReason!, StringComparison.Ordinal);
        var repeated = new NzbWebDAV.Models.Nzb.NzbFile { Subject = "normalized duplicate" };
        repeated.Segments.Add(new NzbWebDAV.Models.Nzb.NzbSegment
            { Bytes = 100, Number = 1, MessageId = " <direct-1@test> " });
        document.Files.Add(repeated);
        var duplicate = new LegacyIdentityExtractor().Extract(row with
            { NzbSegmentsJson = "[\"direct-1@test\",\"direct-2@test\"]" }, document, "release");
        Assert.Equal("excluded", duplicate.ExtractionStatus);
    }

    [Fact]
    public async Task ArchiveIdentity_UsesArticleRangesAndSurvivesPublishedRename()
    {
        await using var stream = File.OpenRead(Path.Join(AppContext.BaseDirectory,
            "Fixtures", "UsenetMigration", "direct-sample.nzb"));
        var document = await NzbWebDAV.Models.Nzb.NzbDocument.LoadAsync(stream);
        var metadata = JsonSerializer.Serialize(new[]
        {
            new NzbWebDAV.Database.Models.DavRarFile.RarPart
            {
                SegmentIds = ["direct-1@test", "direct-2@test"],
                PartSize = 220,
                Offset = 10,
                ByteCount = 99,
            },
        });
        var row = JsonSerializer.Deserialize<LegacyDavItemRow>(JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), Path = "/content/tv/job (2)/nested/a.mkv", FileSize = 99L, Type = 4,
            NzbBlobId = Guid.NewGuid(), HistoryJobName = "job", ReleaseRootPath = "/content/tv/job (2)",
            RarPartsJson = metadata,
        }))!;
        var leaf = new LegacyIdentityExtractor().Extract(row, document, "release");
        var renamed = new LegacyIdentityExtractor().Extract(row with
        {
            Path = "/content/tv/renamed-release/future-name.mkv",
            ReleaseRootPath = "/content/tv/renamed-release",
        }, document, "release");
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(document.Files.Select(file =>
            file.Segments.Select(segment => new NzbDavArticleSegment(segment.Number, segment.Bytes, segment.MessageId))));
        Assert.Equal("ready", leaf.ExtractionStatus);
        Assert.Equal(NzbDavStableArchiveIdentity.Kind, leaf.IdentityKind);
        Assert.Equal(leaf.IdentityDigest, renamed.IdentityDigest);
        Assert.Equal(NzbDavStableArchiveIdentity.Compute(releaseDigest,
        [
            new NzbDavArchivePartIdentity(
                document.Files.SelectMany(file => file.Segments)
                    .Select(segment => new NzbDavArticleSegment(segment.Number, segment.Bytes, segment.MessageId))
                    .ToArray(),
                SegmentStart: 0,
                SegmentLength: 220,
                FileStart: 10,
                FileLength: 99),
        ], 99), leaf.IdentityDigest);
    }

    [Fact]
    public void Inventory_InlineNzbQualifiesButMissingHistoryIsExcluded()
    {
        var history = Guid.NewGuid();
        var id = Guid.NewGuid();
        var row = JsonSerializer.Deserialize<LegacyDavItemRow>(JsonSerializer.Serialize(new
        {
            Id = id, Path = "/content/release/a.mkv", FileSize = 12, Type = 3,
            HistoryItemId = history, NzbBlobId = history, NzbContents = "<nzb />", HistoryDownloadStatus = 1,
        }))!;
        var service = new LibraryInventoryService();
        var links = new[] { new LibraryInventoryLink("a.mkv", "/.ids/a", id) };
        var candidate = Assert.Single(service.Enrich(links, [row], new LegacyBlobResolver(_root)));
        Assert.Equal("candidate", candidate.Status);
        var orphan = Assert.Single(service.Enrich(links, [row with { HistoryItemId = null }], new LegacyBlobResolver(_root)));
        Assert.Equal("excluded", orphan.Status);
        Assert.Equal("missing-history", orphan.ExclusionReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
