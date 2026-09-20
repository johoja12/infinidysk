using NzbDavMigration.Canary;
using NzbWebDAV.UsenetMigration.Canary;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryPerformanceProbeTests
{
    [Fact]
    public async Task ProbeAsync_UsesRequiredSeekOffsetsAndBoundedByteWindows()
    {
        const int size = 16 * 1024 * 1024;
        var item = Selection(size);
        var route = new CanaryRouteDescription("http://backend:8080", "direct-backend");

        var observations = await new CanaryPerformanceProbe().ProbeAsync(
            item, "infinidysk", "first-pass", route,
            _ => new MemoryStream(new byte[size], writable: false),
            TimeSpan.FromSeconds(2));

        Assert.Equal(4, observations.Count);
        Assert.Equal([size / 10L, size / 2L, size * 9L / 10L],
            observations.Where(row => row.Operation.StartsWith("seek-", StringComparison.Ordinal))
                .Select(row => row.Offset));
        Assert.Equal(8 * 1024 * 1024, observations.Single(row => row.Operation == "seek-10").RequestedBytes);
        Assert.Equal(size - (size * 9L / 10L),
            observations.Single(row => row.Operation == "seek-90").ActualBytes);
        var sequential = observations.Single(row => row.Operation == "sequential");
        Assert.Equal(size, sequential.RequestedBytes);
        Assert.Equal(size, sequential.ActualBytes);
    }

    [Fact]
    public async Task ProbeAsync_RecordsTimeoutAndDoesNotHideShortReads()
    {
        var item = Selection(256 * 1024 * 1024);
        var route = new CanaryRouteDescription("http://frontend:3000", "frontend-proxied");
        var timeoutRows = await new CanaryPerformanceProbe().ProbeAsync(
            item, "infinidysk", "repeat-pass", route,
            _ => new BlockingReadStream(item.ExpectedFileSize),
            TimeSpan.FromMilliseconds(10));
        Assert.All(timeoutRows, row => Assert.True(row.TimedOut));
        Assert.All(timeoutRows, row => Assert.True(row.DiagnosticOnly));

        var shortRows = await new CanaryPerformanceProbe().ProbeAsync(
            item with { ExpectedFileSize = 1024 }, "legacy", "first-pass",
            new CanaryRouteDescription("http://legacy:8080", "direct-backend"),
            _ => new MemoryStream(new byte[100], writable: false),
            TimeSpan.FromSeconds(1));
        Assert.Equal(100, shortRows.Single(row => row.Operation == "sequential").ActualBytes);
        Assert.All(shortRows, row => Assert.True(row.ActualBytes < row.RequestedBytes));
        Assert.All(shortRows, row => Assert.NotNull(row.Error));
    }

    [Fact]
    public void Selection_RequiresExactlySixUniqueManuallyReviewedExactFiles()
    {
        var valid = Enumerable.Range(0, 6).Select(index => Selection(100 + index) with
        {
            LibraryRelativePath = $"TV/{index}.mkv",
            LegacyDavItemId = Guid.NewGuid(),
            InfiniDyskDavItemId = Guid.NewGuid(),
            IsLargeFileCase = index == 0,
        }).ToArray();
        new CanaryBenchmarkSelection(1, "reviewed-manual", valid).Validate();

        Assert.Throws<InvalidDataException>(() =>
            new CanaryBenchmarkSelection(1, "automatic", valid).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new CanaryBenchmarkSelection(1, "reviewed-manual", valid[..5]).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new CanaryBenchmarkSelection(1, "reviewed-manual", [.. valid[..5], valid[0]]).Validate());

        var links = valid.Select(file => new NzbDavCanaryPlanLink(
            file.LibraryRelativePath, "/legacy/source", file.LegacyDavItemId,
            file.ExpectedFileSize, "exact", "{}", $".ids/a/b/c/d/e/{file.InfiniDyskDavItemId}", "planned"))
            .ToArray();
        var plan = new NzbDavCanaryPlan(1, 1, new string('c', 64), DateTimeOffset.UtcNow, 6, 6, true, links);
        CanaryBenchmarkSelectionReader.ValidateAgainstPlan(
            new CanaryBenchmarkSelection(1, "reviewed-manual", valid), plan);
        Assert.Throws<InvalidDataException>(() => CanaryBenchmarkSelectionReader.ValidateAgainstPlan(
            new CanaryBenchmarkSelection(1, "reviewed-manual", [valid[0] with { ExpectedFileSize = 999 }, .. valid[1..]]),
            plan));
    }

    [Fact]
    public async Task ProbeAsync_UsesInjectedMonotonicClockForTtfbAndCompletion()
    {
        var clock = new AdvancingClock();
        var item = Selection(100);
        var rows = await new CanaryPerformanceProbe(clock).ProbeAsync(
            item, "legacy", "first-pass",
            new CanaryRouteDescription("http://legacy:8080", "direct-backend"),
            _ => new MemoryStream(new byte[100], writable: false),
            TimeSpan.FromSeconds(1));

        var sequential = rows.Single(row => row.Operation == "sequential");
        Assert.Equal(10, sequential.TimeToFirstByteMilliseconds);
        Assert.Equal(20, sequential.DurationMilliseconds);
    }

    [Fact]
    public async Task ProbeAsync_PropagatesCallerCancellationAndKeepsSidesIndependent()
    {
        var item = Selection(100);
        var route = new CanaryRouteDescription("http://backend:8080", "direct-backend");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CanaryPerformanceProbe().ProbeAsync(
                item, "legacy", "first-pass", route,
                _ => new BlockingReadStream(100), TimeSpan.FromSeconds(1),
                cancellationToken: cancelled.Token));

        var legacy = await new CanaryPerformanceProbe().ProbeAsync(
            item, "legacy", "first-pass", route,
            _ => throw new IOException("legacy unavailable"), TimeSpan.FromSeconds(1));
        var infini = await new CanaryPerformanceProbe().ProbeAsync(
            item, "infinidysk", "first-pass", route,
            _ => new MemoryStream(new byte[100], writable: false), TimeSpan.FromSeconds(1));
        Assert.All(legacy, row => Assert.Contains("legacy unavailable", row.Error));
        Assert.All(infini, row => Assert.Null(row.Error));
    }

    private static CanaryBenchmarkFile Selection(long size) => new(
        "TV/sample.mkv", Guid.NewGuid(), Guid.NewGuid(), size,
        "direct", "1080p", true);

    private sealed class BlockingReadStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => Position = offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class AdvancingClock : ICanaryMonotonicClock
    {
        private long _timestamp = -10;
        public long GetTimestamp() => Interlocked.Add(ref _timestamp, 10);
        public TimeSpan GetElapsed(long start, long end) => TimeSpan.FromMilliseconds(end - start);
    }
}
