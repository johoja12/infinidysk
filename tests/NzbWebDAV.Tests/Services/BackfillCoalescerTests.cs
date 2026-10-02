using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

public sealed class BackfillCoalescerTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void Requests_WaitUntilTheItemIsQuiet_ThenFlushAsOneMergedRange()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UtcNow);
        var coalescer = new BackfillCoalescer(clock);
        var item = Guid.NewGuid();
        // Six separate 4 MiB skips from one playback session.
        for (var block = 0; block < 6; block++)
        {
            coalescer.Add(item, block * 12 * MiB, 4 * MiB);
            clock.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.Empty(coalescer.TakeDue()); // Still within the quiet window of the last request.

        clock.Advance(BackfillCoalescer.Quiet);
        var (flushed, ranges) = Assert.Single(coalescer.TakeDue());
        Assert.Equal(item, flushed);
        Assert.Equal((0L, 64 * MiB), Assert.Single(ranges));
        Assert.Equal(0, coalescer.PendingItems);
    }

    [Fact]
    public void ContinuousRequests_FlushAfterTheMaximumDelay()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UtcNow);
        var coalescer = new BackfillCoalescer(clock);
        var item = Guid.NewGuid();
        var elapsed = TimeSpan.Zero;
        while (elapsed < BackfillCoalescer.MaxDelay)
        {
            coalescer.Add(item, 0, 4 * MiB);
            Assert.Empty(coalescer.TakeDue());
            clock.Advance(TimeSpan.FromSeconds(30));
            elapsed += TimeSpan.FromSeconds(30);
        }
        Assert.Single(coalescer.TakeDue());
    }

    [Fact]
    public void DistantRanges_StayBoundedAndSeparate()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UtcNow);
        var coalescer = new BackfillCoalescer(clock);
        var item = Guid.NewGuid();
        for (var index = 0; index < 20; index++)
            coalescer.Add(item, index * 1024 * MiB, 4 * MiB); // 1 GiB apart: never one union.
        var (_, ranges) = Assert.Single(coalescer.TakeDue(all: true));
        Assert.Equal(BackfillCoalescer.MaxRangesPerItem, ranges.Count);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(19 * 1024 * MiB + 4 * MiB, ranges[^1].Start + ranges[^1].Length);
    }

    [Fact]
    public void ItemsFlushIndependently_AndInvalidRequestsAreIgnored()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UtcNow);
        var coalescer = new BackfillCoalescer(clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coalescer.Add(first, 0, 4 * MiB);
        coalescer.Add(Guid.Empty, 0, 4 * MiB);
        coalescer.Add(second, -1, 4 * MiB);
        coalescer.Add(second, 0, 0);
        clock.Advance(BackfillCoalescer.Quiet);
        coalescer.Add(second, 0, 4 * MiB);
        Assert.Equal(first, Assert.Single(coalescer.TakeDue()).ItemId);
        Assert.Equal(second, Assert.Single(coalescer.TakeDue(all: true)).ItemId);
    }
}
