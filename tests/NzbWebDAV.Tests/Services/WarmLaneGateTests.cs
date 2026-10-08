using NzbWebDAV.Services;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class WarmLaneGateTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void ShortProbeReads_DoNotParkExtraLanes()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = new ActiveReadRegistry();
        for (var index = 0; index < 5; index++)
        {
            var id = reads.GetOrCreate($"/.ids/probe-{index}", "rclone", $"probe-{index}.mkv", 20_000 * MiB, null, null, null, now.AddSeconds(-5));
            reads.Touch(id, 40 * MiB, 40 * MiB, now.AddSeconds(-1));
        }

        Assert.True(NativePrefetchExecutor.ExtraLanesMayRun(reads.Snapshot(), plexPlayback: false, freeSlots: 8, capacity: 8, now));
    }

    [Fact]
    public void SustainedSequentialRead_ParksExtraLanes()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = new ActiveReadRegistry();
        var id = reads.GetOrCreate("/.ids/movie", "rclone", "movie.mkv", 20_000 * MiB, null, null, null, now.AddSeconds(-40));
        var offset = 0L;
        foreach (var secondsAgo in new[] { 40, 30, 20, 10, 1 })
            reads.Touch(id, 16 * MiB, offset += 16 * MiB, now.AddSeconds(-secondsAgo));

        Assert.False(NativePrefetchExecutor.ExtraLanesMayRun(reads.Snapshot(), plexPlayback: false, freeSlots: 8, capacity: 8, now));
    }

    [Theory]
    [InlineData(true, 8)]
    [InlineData(false, 3)]
    public void PlexPlaybackOrShortBuffers_ParkExtraLanes(bool plexPlayback, int freeSlots)
        => Assert.False(NativePrefetchExecutor.ExtraLanesMayRun([], plexPlayback, freeSlots, capacity: 8, DateTimeOffset.UtcNow));
}
