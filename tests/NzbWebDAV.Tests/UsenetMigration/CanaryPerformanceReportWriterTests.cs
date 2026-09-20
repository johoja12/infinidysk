using NzbDavMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryPerformanceReportWriterTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"canary-report-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteAsync_PreservesFailedRowsAndActualSideBySideValues()
    {
        var file = new CanaryBenchmarkFile(
            "TV/failed.mkv", Guid.NewGuid(), Guid.NewGuid(), 1024, "rar-multipart", "4k", true);
        var metadata = new CanaryBenchmarkMetadata(
            new CanaryRouteDescription("http://legacy:8080", "direct-backend"),
            new CanaryRouteDescription("http://infinidysk:3000", "frontend-proxied"));
        var rows = new[]
        {
            new CanaryPerformanceObservation(
                file.LibraryRelativePath, "legacy", "first-pass", "seek-10", 102, 512, 512,
                1.25, 4.5, 0.11, "cache-state-unknown-first-pass", metadata.LegacyRoute,
                false, false, null),
            new CanaryPerformanceObservation(
                file.LibraryRelativePath, "infinidysk", "first-pass", "seek-10", 102, 512, 0,
                null, 2000, null, "cache-state-unknown-first-pass", metadata.InfiniDyskRoute,
                true, true, "timed out"),
        };

        await new CanaryPerformanceReportWriter().WriteAsync(
            _root, new CanaryPerformanceResults(DateTimeOffset.UtcNow, metadata, [file], rows));

        var json = await File.ReadAllTextAsync(Path.Join(_root, "performance-results.json"));
        var markdown = await File.ReadAllTextAsync(Path.Join(_root, "performance-results.md"));
        Assert.Contains("timed out", json, StringComparison.Ordinal);
        Assert.Contains("timed out", markdown, StringComparison.Ordinal);
        Assert.Contains("1.25", markdown, StringComparison.Ordinal);
        Assert.Contains("frontend-proxied", markdown, StringComparison.Ordinal);
        Assert.Contains("diagnostic", markdown, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
