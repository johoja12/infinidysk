using NzbWebDAV.Services;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class WarmLaneGateTests
{
    private const long MiB = 1024 * 1024;
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Func<Guid, bool> NothingCached = _ => false;

    private static bool MayRun(IReadOnlyList<ActiveReadRegistry.Entry> reads, PlexPlaybackRegistry.Load load = PlexPlaybackRegistry.Load.None,
        bool firstLane = false, Func<Guid, bool>? cached = null, int freeSlots = 8, DateTimeOffset? now = null)
        => NativePrefetchExecutor.ExtraLanesMayRun(Item, firstLane, reads, load, cached ?? NothingCached, freeSlots,
            capacity: 8, now ?? DateTimeOffset.UtcNow);

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

        Assert.True(MayRun(reads.Snapshot(), now: now));
    }

    [Fact]
    public void SustainedSequentialRead_ParksExtraLanes()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(MayRun(SustainedRead(Guid.NewGuid(), now).Snapshot(), now: now, firstLane: true));
        Assert.False(MayRun(SustainedRead(null, now).Snapshot(), now: now, firstLane: true));
    }

    [Theory]
    [InlineData(PlexPlaybackRegistry.Load.Other, 8)]
    [InlineData(PlexPlaybackRegistry.Load.None, 3)]
    public void OtherPlexPlaybackOrShortBuffers_ParkEveryExtraLane(PlexPlaybackRegistry.Load load, int freeSlots)
    {
        Assert.False(MayRun([], load, freeSlots: freeSlots));
        Assert.False(MayRun([], load, firstLane: true, freeSlots: freeSlots));
    }

    [Fact]
    public void PlaybackOfThisFile_LetsOnlyTheFirstExtraLaneRun()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = SustainedRead(Item, now).Snapshot();

        Assert.True(MayRun(reads, PlexPlaybackRegistry.Load.OnlyItem, firstLane: true, now: now));
        Assert.False(MayRun(reads, PlexPlaybackRegistry.Load.OnlyItem, now: now));
        Assert.True(MayRun(reads, firstLane: true, now: now));
        Assert.True(MayRun([], PlexPlaybackRegistry.Load.OnlyItem, firstLane: true));
        Assert.False(MayRun([], PlexPlaybackRegistry.Load.OnlyItem));
    }

    [Fact]
    public void PlaybackOfAFullyCachedFile_DoesNotParkExtraLanes()
    {
        var now = DateTimeOffset.UtcNow;
        var cachedItem = Guid.NewGuid();
        var reads = SustainedRead(cachedItem, now).Snapshot();

        Assert.True(MayRun(reads, cached: id => id == cachedItem, now: now));
        Assert.False(MayRun(reads, now: now));
    }

    private static ActiveReadRegistry SustainedRead(Guid? item, DateTimeOffset now)
    {
        var reads = new ActiveReadRegistry();
        var id = reads.GetOrCreate("/.ids/movie", "rclone", "movie.mkv", 20_000 * MiB, null, null, null, now.AddSeconds(-40));
        if (item is { } itemId) reads.UpdateInfo(id, null, null, itemId: itemId);
        var offset = 0L;
        foreach (var secondsAgo in new[] { 40, 30, 20, 10, 1 })
            reads.Touch(id, 16 * MiB, offset += 16 * MiB, now.AddSeconds(-secondsAgo));
        return reads;
    }
}
