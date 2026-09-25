using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Recovery;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Source;
using System.Security.Cryptography;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class ShardedMappedExporterTests
{
    [Fact]
    public void ValidatePair_RequiresPerRootAndCombinedCoverage()
    {
        var plex = Evidence("/mnt/plex", 100, 90, [Guid.NewGuid()], [Digest('a')]);
        var special = Evidence("/mnt/special", 10, 9, [Guid.NewGuid()], [Digest('b')]);

        var valid = ShardedMappedExporter.ValidatePair(plex, special);
        Assert.Equal(99, valid.CombinedRecoverableRows);
        Assert.Equal(0.9m, valid.CombinedRecoveryFraction);
        Assert.Throws<InvalidDataException>(() => ShardedMappedExporter.ValidatePair(
            plex, Evidence("/mnt/special", 10, 8, [Guid.NewGuid()], [Digest('b')])));
    }

    [Fact]
    public void ValidatePair_RejectsSharedMappedIdsAndPayloads()
    {
        var sharedId = Guid.NewGuid();
        var plex = Evidence("/mnt/plex", 100, 90, [sharedId], [Digest('a')]);

        Assert.Throws<InvalidDataException>(() => ShardedMappedExporter.ValidatePair(
            plex, Evidence("/mnt/special", 10, 9, [sharedId], [Digest('b')])));
        Assert.Throws<InvalidDataException>(() => ShardedMappedExporter.ValidatePair(
            plex, Evidence("/mnt/special", 10, 9, [Guid.NewGuid()], [Digest('a')])));
    }

    [Fact]
    public void ValidateSpecialAhead_RequiresCompleteSpecialRecoveryAndDistinctMappedIds()
    {
        var specialId = Guid.NewGuid();
        var special = Evidence("/mnt/special", 100, 98, [specialId], [Digest('a')]);
        var plex = Evidence("/mnt/plex", 1000, 1, [Guid.NewGuid()], [Digest('b')]);

        ShardedMappedExporter.ValidateSpecialAhead(special, plex.Inventory, plex.DavItemIds);
        Assert.Throws<InvalidDataException>(() => ShardedMappedExporter.ValidateSpecialAhead(
            Evidence("/mnt/special", 100, 89, [specialId], [Digest('a')]),
            plex.Inventory, plex.DavItemIds));
        Assert.Throws<InvalidDataException>(() => ShardedMappedExporter.ValidateSpecialAhead(
            special, plex.Inventory, new HashSet<Guid> { specialId }));
        Assert.Throws<InvalidDataException>(() => ShardedMappedExporter.ValidateSpecialAhead(
            special, special.Inventory, plex.DavItemIds));
    }

    [Fact]
    public void SelectCanonicalSpecialScenes_SkipsAReleaseWhoseLegacyJobNameChangesOnSubmission()
    {
        var canonical = new LegacySourceRecoveryItem("scenes/good.mkv", "/legacy/good",
            Guid.NewGuid(), "/content/scenes/good/good.mkv", "exact-direct", null,
            "good.nzb", Digest('a'), NzbDavArticleIdentity.DirectKind, Digest('b'), 100);
        var noncanonical = canonical with
        {
            LibraryRelativePath = "scenes/bad.mkv",
            LegacyDavItemId = Guid.NewGuid(),
            LegacyPath = "/content/scenes/bad:job/bad.mkv",
            PayloadSha256 = Digest('c'),
        };

        var selected = ShardedMappedExporter.SelectCanonicalSpecialScenes(
            [canonical, noncanonical], out var skipped);

        Assert.Single(selected);
        Assert.Equal(canonical.LegacyDavItemId, selected[0].LegacyDavItemId);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public async Task ExportVerified_ResumesOnlyMatchingChecksummedMappedPackages()
    {
        var fixture = Path.Join(Path.GetTempPath(), $"sharded-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixture);
        try
        {
            var payloadRoot = Path.Join(fixture, "payloads");
            Directory.CreateDirectory(payloadRoot);
            var payloadPath = Path.Join(payloadRoot, "mixed.nzb");
            File.Copy(Path.Join(AppContext.BaseDirectory, "Fixtures", "UsenetMigration",
                "direct-sample.nzb"), payloadPath);
            var payloadDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payloadPath)))
                .ToLowerInvariant();
            var id = Guid.NewGuid();
            var source = Evidence("/mnt/plex", 1, 1, [id], [payloadDigest]);
            var selected = new LegacySourceRecoveryItem("Movie/movie.mkv", "/legacy/.ids/" + id,
                id, "/content/tv/Movie/movie.mkv", "exact-direct", null, "mixed.nzb", payloadDigest,
                NzbDavArticleIdentity.DirectKind, Digest('a'), 100);
            source = source with { Items = [selected] };
            var peerPayloadPath = Path.Join(payloadRoot, "special.nzb");
            await File.WriteAllTextAsync(peerPayloadPath,
                await File.ReadAllTextAsync(payloadPath) + "\n");
            var peerPayloadDigest = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(peerPayloadPath))).ToLowerInvariant();
            var peerId = Guid.NewGuid();
            var peer = Evidence("/mnt/special", 1, 1, [peerId], [peerPayloadDigest]);
            peer = peer with { Items = [new LegacySourceRecoveryItem(
                "Show/episode.mkv", "/legacy/.ids/" + peerId, peerId,
                "/content/tv/Show/episode.mkv", "exact-direct", null,
                "special.nzb", peerPayloadDigest, NzbDavArticleIdentity.DirectKind,
                Digest('b'), 100)] };
            var pair = ShardedMappedExporter.ValidatePair(source, peer);
            var output = Path.Join(fixture, "batches");
            var peerOutput = Path.Join(fixture, "special-batches");
            var exporter = new ShardedMappedExporter();

            Assert.Equal(1, await exporter.ExportVerifiedAsync(pair, true, payloadRoot, output));
            Assert.Equal(1, await exporter.ExportVerifiedAsync(pair, true, payloadRoot, output));
            Assert.Equal(1, await exporter.ExportVerifiedAsync(pair, false, payloadRoot, peerOutput));
            var package = await new NzbDavPackageReader().ReadAsync(Path.Join(output, "batch-0001"));
            var peerPackage = await new NzbDavPackageReader().ReadAsync(
                Path.Join(peerOutput, "batch-0001"));
            Assert.Single(package.Manifest.SelectedLinks);
            Assert.Single(package.Manifest.Releases.Single().Leaves);
            Assert.Equal(0, package.Manifest.BatchIndex);
            Assert.Equal(0, peerPackage.Manifest.BatchIndex);
            Assert.NotEqual(package.Manifest.MasterManifestDigest,
                peerPackage.Manifest.MasterManifestDigest);

            await File.AppendAllTextAsync(Path.Join(output, "batch-0001", "manifest.json"), " ");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                exporter.ExportVerifiedAsync(pair, true, payloadRoot, output));
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }
    }

    private static ShardedMappedRootEvidence Evidence(
        string root, int count, int recovered, Guid[] ids, string[] payloads)
    {
        var inventory = new ShardedMappedInventoryManifest(1, root, root + "/.ids",
            DateTimeOffset.UtcNow, count, 0, Digest('1'), Digest('2'), []);
        var recovery = new ShardedMappedRecoveryManifest(1, DateTimeOffset.UtcNow,
            root, root + "/.ids", Digest('1'), Digest('3'), count, recovered,
            decimal.Divide(recovered, count), []);
        return new ShardedMappedRootEvidence(inventory, recovery, [],
            ids.ToHashSet(), payloads.ToHashSet(StringComparer.Ordinal));
    }

    private static string Digest(char character) => new(character, 64);
}
