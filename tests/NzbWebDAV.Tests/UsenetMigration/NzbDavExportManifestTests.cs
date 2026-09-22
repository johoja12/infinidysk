using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavExportManifestTests
{
    [Fact]
    public void SerializeRoundTrip_PreservesVersionedPackageContract()
    {
        var manifest = SampleManifest();

        var json = NzbDavExportManifestJson.Serialize(manifest);
        var restored = NzbDavExportManifestJson.Deserialize(json);

        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal("canary-20260920", restored.PackageId);
        Assert.Equal("release-1", Assert.Single(restored.Releases).SourceReleaseId);
        Assert.Equal("Outlander.Blood.of.My.Blood.S02E01.nzb",
            Assert.Single(restored.Releases).SourceFileName);
        Assert.Equal("Outlander.Blood.of.My.Blood.S02E01",
            Assert.Single(restored.Releases).SourceJobName);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Assert.Single(restored.Releases[0].Leaves).LegacyDavItemId);
        Assert.Equal("payloads/release-1.nzb", Assert.Single(restored.Payloads).RelativePath);
        Assert.Equal("TV-HD/Show/episode.mkv", Assert.Single(restored.SelectedLinks).LibraryRelativePath);
        Assert.Equal(64, Assert.Single(restored.Checksums).Sha256.Length);
    }

    [Fact]
    public void Serialize_IsDeterministicForSameManifest()
    {
        var manifest = SampleManifest();

        Assert.Equal(
            NzbDavExportManifestJson.Serialize(manifest),
            NzbDavExportManifestJson.Serialize(manifest));
    }

    [Fact]
    public void Deserialize_RejectsUnknownMajorVersion()
    {
        var json = NzbDavExportManifestJson.Serialize(SampleManifest())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 3", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(
            () => NzbDavExportManifestJson.Deserialize(json));

        Assert.Contains("schema version 3", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/absolute/release.nzb")]
    [InlineData("../outside.nzb")]
    [InlineData("payloads/../../outside.nzb")]
    [InlineData("payloads\\release.nzb")]
    [InlineData("payloads//release.nzb")]
    [InlineData("")]
    public void Validate_RejectsUnsafePackageRelativePath(string relativePath)
    {
        var manifest = SampleManifest() with
        {
            Payloads =
            [
                new NzbDavPayloadFile(relativePath, 123, new string('a', 64)),
            ],
        };

        Assert.Throws<InvalidDataException>(() => manifest.Validate());
    }

    [Theory]
    [InlineData("Outlander.S02E01.nzb", null)]
    [InlineData(null, "Outlander.S02E01")]
    [InlineData("../Outlander.S02E01.nzb", "Outlander.S02E01")]
    [InlineData("Outlander.S02E01.nzb", " ")]
    [InlineData("Outlander.S02E01.nzb", "Different.Release")]
    public void Validate_RejectsInvalidSourceNamingMetadata(string? sourceFileName, string? sourceJobName)
    {
        var sourceRelease = Assert.Single(SampleManifest().Releases);
        var manifest = SampleManifest() with
        {
            Releases =
            [
                sourceRelease with
                {
                    SourceFileName = sourceFileName,
                    SourceJobName = sourceJobName,
                },
            ],
        };

        Assert.Throws<InvalidDataException>(() => manifest.Validate());
    }

    private static NzbDavExportManifest SampleManifest() => new(
        SchemaVersion: 1,
        PackageId: "canary-20260920",
        CreatedAt: new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero),
        Source: "nzbdav",
        Releases:
        [
            new NzbDavExportRelease(
                SourceReleaseId: "release-1",
                NzbBlobId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                PayloadPath: "payloads/release-1.nzb",
                Leaves:
                [
                    new NzbDavExportLeaf(
                        LegacyDavItemId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        LegacyPath: "/content/tv/Show/episode.mkv",
                        FileSize: 1_000,
                        ParentReleaseId: "release-1",
                        HistoryId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        NzbBlobId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        IdentityKind: NzbDavArticleIdentity.DirectKind,
                        IdentityDigest: new string('b', 64),
                        ExtractionStatus: "ready",
                        ExclusionReason: null),
                ],
                SourceFileName: "Outlander.Blood.of.My.Blood.S02E01.nzb",
                SourceJobName: "Outlander.Blood.of.My.Blood.S02E01"),
        ],
        SelectedLinks:
        [
            new NzbDavSelectedLibraryLink(
                "TV-HD/Show/episode.mkv",
                "/mnt/remote/nzbdav/.ids/1/1/1/1/1/11111111-1111-1111-1111-111111111111",
                Guid.Parse("11111111-1111-1111-1111-111111111111")),
        ],
        Payloads:
        [
            new NzbDavPayloadFile("payloads/release-1.nzb", 123, new string('a', 64)),
        ],
        Checksums:
        [
            new NzbDavChecksumEntry("payloads/release-1.nzb", new string('a', 64)),
        ]);
}
