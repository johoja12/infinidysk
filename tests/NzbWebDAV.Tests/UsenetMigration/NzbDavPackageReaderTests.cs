using System.Security.Cryptography;
using System.Text;
using NzbDavMigration.Export;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavPackageReaderTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-package-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadAsync_VerifiesValidPackageBeforeReturningPayloads()
    {
        var package = await CreatePackageAsync();

        var result = await new NzbDavPackageReader().ReadAsync(package);

        Assert.Equal("test-package", result.Manifest.PackageId);
        Assert.Single(result.PayloadPaths);
        Assert.Equal(64, result.PackageDigest.Length);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("traversal")]
    [InlineData("unknown-version")]
    [InlineData("duplicate-id")]
    [InlineData("inconsistent-release")]
    [InlineData("missing-payload")]
    [InlineData("symlink-payload")]
    public async Task ReadAsync_FailsClosedForTamperedPackages(string tamper)
    {
        var package = await CreatePackageAsync();
        var manifestPath = Path.Join(package, "manifest.json");
        var payloadPath = Path.Join(package, "payloads", "release-1.nzb");
        switch (tamper)
        {
            case "checksum":
                await File.AppendAllTextAsync(payloadPath, "tampered");
                break;
            case "traversal":
                await RewriteManifestAsync(package, json => json.Replace(
                    "payloads/release-1.nzb", "../release-1.nzb", StringComparison.Ordinal));
                break;
            case "unknown-version":
                await RewriteManifestAsync(package, json => json.Replace(
                    "\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
                break;
            case "duplicate-id":
                await RewriteTypedManifestAsync(package, manifest => manifest with
                {
                    Releases = [manifest.Releases[0] with
                    {
                        Leaves = [manifest.Releases[0].Leaves[0], manifest.Releases[0].Leaves[0]],
                    }],
                });
                break;
            case "inconsistent-release":
                await RewriteTypedManifestAsync(package, manifest => manifest with
                {
                    Releases = [manifest.Releases[0] with
                    {
                        Leaves = [manifest.Releases[0].Leaves[0] with { ParentReleaseId = "other" }],
                    }],
                });
                break;
            case "missing-payload":
                File.Delete(payloadPath);
                break;
            case "symlink-payload":
                var target = Path.Join(_root, "target.nzb");
                File.Move(payloadPath, target);
                File.CreateSymbolicLink(payloadPath, target);
                break;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new NzbDavPackageReader().ReadAsync(package));
        Assert.True(File.Exists(manifestPath));
    }

    private async Task<string> CreatePackageAsync()
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Join(_root, $"source-{Guid.NewGuid():N}.nzb");
        await File.WriteAllTextAsync(payload, """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="sample"><segments><segment bytes="10" number="1">one@test</segment></segments></file>
            </nzb>
            """);
        var package = Path.Join(_root, $"package-{Guid.NewGuid():N}");
        var leafId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            "test-package",
            package,
            [new CanaryExportRelease("release-1", blobId, payload,
                [new NzbDavExportLeaf(leafId, "/content/a.mkv", 10, "release-1", null, blobId,
                    NzbDavArticleIdentity.DirectKind, new string('a', 64), "ready", null)])],
            [new NzbDavSelectedLibraryLink("Migration-TV/a.mkv", "/legacy/.ids/a", leafId)]));
        return package;
    }

    private static async Task RewriteTypedManifestAsync(
        string package,
        Func<NzbDavExportManifest, NzbDavExportManifest> mutate)
    {
        var path = Path.Join(package, "manifest.json");
        var manifest = NzbDavExportManifestJson.Deserialize(await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, NzbDavExportManifestJson.Serialize(mutate(manifest)));
        await RefreshManifestChecksumAsync(package);
    }

    private static async Task RewriteManifestAsync(string package, Func<string, string> mutate)
    {
        var path = Path.Join(package, "manifest.json");
        await File.WriteAllTextAsync(path, mutate(await File.ReadAllTextAsync(path)));
        await RefreshManifestChecksumAsync(package);
    }

    private static async Task RefreshManifestChecksumAsync(string package)
    {
        var manifestPath = Path.Join(package, "manifest.json");
        await using var stream = File.OpenRead(manifestPath);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        var sumsPath = Path.Join(package, "SHA256SUMS");
        var lines = (await File.ReadAllLinesAsync(sumsPath))
            .Select(line => line.EndsWith("  manifest.json", StringComparison.Ordinal)
                ? $"{digest}  manifest.json"
                : line);
        await File.WriteAllTextAsync(sumsPath, string.Join('\n', lines) + "\n", Encoding.UTF8);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
