using System.Text.Json;
using NzbDavMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryCacheEvidenceTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(), $"canary-cache-evidence-{Guid.NewGuid():N}");

    [Fact]
    public void Classify_UsesNormalizedMetadataRangesInsteadOfSparseLogicalLength()
    {
        var fixture = CreateFixture(1_000, [new(0, 100), new(50, 100), new(150, 50)]);

        var result = CanaryCacheEvidence.Classify(
            fixture.CacheRoot, fixture.LibraryPath, 1_000);

        Assert.NotNull(result);
        Assert.Equal("observed-partial", result.Label);
        Assert.Equal(200, result.CachedBytes);
        Assert.Equal(1_000, result.ExpectedBytes);
        Assert.Equal(20d, result.CoveragePercent, 6);
        Assert.Equal("rclone-vfs-meta", result.Source);
    }

    [Fact]
    public void Classify_RequiresCompleteCoverageBeforeCallingCacheWarm()
    {
        var fixture = CreateFixture(1_000, [new(0, 500), new(500, 500)]);

        var result = CanaryCacheEvidence.Classify(
            fixture.CacheRoot, fixture.LibraryPath, 1_000);

        Assert.NotNull(result);
        Assert.Equal("observed-warm", result.Label);
        Assert.Equal(1_000, result.CachedBytes);
        Assert.Equal(100d, result.CoveragePercent, 6);
    }

    [Fact]
    public void Classify_ReturnsColdWhenNeitherDataNorMetadataExists()
    {
        var fixture = CreateFixture(
            1_000, [], createCacheFiles: false, createMetadata: false);

        var result = CanaryCacheEvidence.Classify(
            fixture.CacheRoot, fixture.LibraryPath, 1_000);

        Assert.NotNull(result);
        Assert.Equal("observed-cold", result.Label);
        Assert.Equal(0, result.CachedBytes);
        Assert.Equal("none", result.Source);
    }

    [Fact]
    public void Classify_FailsClosedWhenOnlyOneCacheArtifactExists()
    {
        var fixture = CreateFixture(1_000, [], createMetadata: false);

        Assert.Null(CanaryCacheEvidence.Classify(
            fixture.CacheRoot, fixture.LibraryPath, 1_000));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"Size\":999,\"Rs\":[]}")]
    [InlineData("{\"Size\":1000,\"Rs\":[{\"Pos\":-1,\"Size\":1}]}")]
    [InlineData("{\"Size\":1000,\"Rs\":[{\"Pos\":999,\"Size\":2}]}")]
    [InlineData("{\"Size\":1000,\"Rs\":[{\"Pos\":9223372036854775807,\"Size\":1}]}")]
    public void Classify_FailsClosedForInvalidMetadata(string metadata)
    {
        var fixture = CreateFixture(1_000, [], rawMetadata: metadata);

        Assert.Null(CanaryCacheEvidence.Classify(
            fixture.CacheRoot, fixture.LibraryPath, 1_000));
    }

    private CacheFixture CreateFixture(
        long size,
        IReadOnlyList<TestRange> ranges,
        bool createCacheFiles = true,
        bool createMetadata = true,
        string? rawMetadata = null)
    {
        var libraryPath = Path.Join(_root, "library", "sample.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        using (var library = File.Create(libraryPath))
            library.SetLength(size);

        var cacheRoot = Path.Join(_root, "cache", "vfs", "remote");
        var metadataRoot = Path.Join(_root, "cache", "vfsMeta", "remote");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(metadataRoot);

        var mount = CanaryPathSafety.GetMountPoint(libraryPath);
        var relative = Path.GetRelativePath(mount, libraryPath);
        var cachePath = Path.Join(cacheRoot, relative);
        var metadataPath = Path.Join(metadataRoot, relative);

        if (createCacheFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var cache = File.Create(cachePath);
            cache.SetLength(size);
        }

        if (createMetadata)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            var metadata = rawMetadata ?? JsonSerializer.Serialize(new
            {
                Size = size,
                Rs = ranges.Select(range => new { range.Pos, range.Size }),
            });
            File.WriteAllText(metadataPath, metadata);
        }

        return new CacheFixture(cacheRoot, libraryPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record CacheFixture(string CacheRoot, string LibraryPath);
    private sealed record TestRange(long Pos, long Size);
}
