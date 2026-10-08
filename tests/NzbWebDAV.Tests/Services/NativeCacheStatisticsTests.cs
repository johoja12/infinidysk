using NzbWebDAV.Services.NativeCache;
using System.Text.Json;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheStatisticsTests
{
    [Fact]
    public void ConcurrentCounters_AreBoundedAggregatesWithoutPathsOrMediaIdentity()
    {
        var statistics = new NativeCacheStatistics();
        Parallel.For(0, 1000, _ =>
        {
            statistics.Hit(4);
            statistics.Miss();
            statistics.Committed(8);
            statistics.Fallback(timeout: true);
        });
        var snapshot = statistics.Snapshot();
        Assert.Equal(1000, snapshot.HitBlocks);
        Assert.Equal(4000, snapshot.HitBytes);
        Assert.Equal(1000, snapshot.MissBlocks);
        Assert.Equal(8000, snapshot.CommittedBytes);
        Assert.Equal(1000, snapshot.Fallbacks);
        Assert.Equal(1000, snapshot.IoTimeouts);
        Assert.DoesNotContain("path", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveWriteSnapshots_IncludeOnlyCommittedBytes_AndReleaseOnDispose()
    {
        var statistics = new NativeCacheStatistics();
        using (var transfer = statistics.BeginTransfer("item", "Film.mkv", 100, background: true))
        {
            Assert.NotNull(transfer);
            Assert.Empty(statistics.ActiveTransfers());
            transfer.Committed(40);
            var active = Assert.Single(statistics.ActiveTransfers());
            Assert.Equal(40, active.CommittedBytes);
            Assert.True(active.Background);
        }
        Assert.Empty(statistics.ActiveTransfers());
    }

    [Fact]
    public void IdleTransfer_LeavesActiveListUntilItCommitsAgain()
    {
        var clock = new ManualClock();
        var statistics = new NativeCacheStatistics(clock);
        using var transfer = statistics.BeginTransfer("item", "Film.mkv", 100, background: true)!;
        transfer.Committed(40);
        clock.Now += NativeCacheStatistics.TransferIdleAfter;
        Assert.Single(statistics.ActiveTransfers());
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Empty(statistics.ActiveTransfers());
        transfer.Committed(20);
        Assert.Equal(60, Assert.Single(statistics.ActiveTransfers()).CommittedBytes);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
