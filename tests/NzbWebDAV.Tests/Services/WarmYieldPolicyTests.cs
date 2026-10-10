using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class WarmYieldPolicyTests
{
    private const double Rate = 4_000_000; // 32 Mbit/s
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static Func<long, CancellationToken, ValueTask<long>> CachedUpTo(long frontier) =>
        (offset, _) => new ValueTask<long>(Math.Max(0, frontier - offset));

    [Fact]
    public async Task Lead_IsTheCachedPlaybackTimeAheadOfTheReaderClosestToTheFrontier()
    {
        var policy = new WarmYieldPolicy(TimeSpan.FromMinutes(10), TimeSpan.Zero);
        policy.Observe([0, 60 * (long)Rate], Now);

        // 20 minutes are cached from the start; the reader one minute in has 19 left.
        var lead = await policy.CachedLeadAsync(Now, servesPlayback: true, Rate, CachedUpTo(20 * 60 * (long)Rate), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(19), lead);
        Assert.True(policy.ShouldYield(lead));
    }

    [Fact]
    public async Task ReaderBetweenBursts_KeepsItsLastPositionAgedByTheTimeSinceItWasSeen()
    {
        var policy = new WarmYieldPolicy(TimeSpan.FromMinutes(10), TimeSpan.Zero);
        policy.Observe([0], Now);
        policy.Observe([], Now.AddMinutes(4));

        var lead = await policy.CachedLeadAsync(Now.AddMinutes(4), servesPlayback: true, Rate, CachedUpTo(12 * 60 * (long)Rate),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(8), lead);
        Assert.False(policy.ShouldYield(lead));
    }

    [Theory]
    [InlineData(true, false)] // Playback with no position seen yet: unknown, so it keeps warming.
    [InlineData(false, true)] // Background warm nobody is reading: nothing is at risk.
    public async Task NoReaderSeen_YieldsOnlyWhenTheWarmServesNoPlayback(bool servesPlayback, bool yields)
    {
        var policy = new WarmYieldPolicy(TimeSpan.FromMinutes(10), TimeSpan.Zero);

        var lead = await policy.CachedLeadAsync(Now, servesPlayback, Rate, CachedUpTo(0), CancellationToken.None);

        Assert.Equal(yields, policy.ShouldYield(lead));
    }

    [Fact]
    public void CheckDue_ThrottlesToTheInterval()
    {
        var policy = new WarmYieldPolicy(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
        Assert.True(policy.CheckDue(Now));
        Assert.False(policy.CheckDue(Now.AddSeconds(4)));
        Assert.True(policy.CheckDue(Now.AddSeconds(5)));
    }

    [Theory]
    [InlineData("backfill", true)]
    [InlineData("read:session", true)]
    [InlineData("plex:server:realtime:user:movie:key:local", true)]
    [InlineData("plex:server:realtime-next:user:episode:key", false)]
    [InlineData("plex:server:history-next:user:episode:key", false)]
    [InlineData("manual", false)]
    public void ServesPlayback_RecognisesOwnersWatchingTheFileNow(string owner, bool expected)
        => Assert.Equal(expected, WarmYieldPolicy.ServesPlayback([owner]));

    [Fact]
    public void PlaybackRate_FallsBackToAHighBitrateWhenTheDurationIsUnknown()
    {
        Assert.Equal(4_000_000, WarmYieldPolicy.PlaybackBytesPerSecond(40_000_000_000, 10_000_000));
        Assert.Equal(WarmYieldPolicy.FallbackBytesPerSecond, WarmYieldPolicy.PlaybackBytesPerSecond(40_000_000_000, null));
    }
}
