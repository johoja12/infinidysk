using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PlexPlaybackRegistryTests
{
    [Fact]
    public void DisabledServers_AreRemovedImmediatelyWithoutWaitingForExpiry()
    {
        var registry = new PlexPlaybackRegistry(TimeProvider.System);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        registry.Record("server", [new PlexSession("session", "user", "playing", media.File, media)], TimeSpan.FromSeconds(300));
        registry.RetainServers([]);
        Assert.False(registry.HasActivePlayback);
    }
    [Fact]
    public void FailedPollCannotRefreshVerifiedPlayback_AndRawReadsAreSeparate()
    {
        var clock = new Clock();
        var registry = new PlexPlaybackRegistry(clock);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        registry.Record("server", [new PlexSession("session", "user", "playing", media.File, media)], TimeSpan.FromSeconds(30));
        Assert.True(registry.HasActivePlayback);
        clock.Now += TimeSpan.FromSeconds(31);
        var raw = new NzbWebDAV.Services.ActiveReadRegistry();
        raw.GetOrCreate("/movie.mkv", "scanner", "movie.mkv", 100);
        Assert.False(registry.HasActivePlayback);
    }

    [Fact]
    public void EmptyFreshSessions_ClearServerPlaybackImmediately()
    {
        var registry = new PlexPlaybackRegistry(TimeProvider.System);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        registry.Record("server", [new PlexSession("session", "user", "playing", media.File, media)], TimeSpan.FromSeconds(30));
        registry.Record("server", [], TimeSpan.FromSeconds(30));
        Assert.False(registry.HasActivePlayback);
    }

    [Fact]
    public void LoadFor_IgnoresCachedPlaybackAndTreatsUnresolvedSessionsAsOtherFiles()
    {
        var registry = new PlexPlaybackRegistry(TimeProvider.System);
        var media = new PlexMediaItem("m", "movie", "Movie", null, null, null, "/movie.mkv", 1, 100, null);
        var playing = new[] { new PlexSession("session", "user", "playing", media.File, media) };
        var item = Guid.NewGuid();
        var cached = Guid.NewGuid();
        Assert.Equal(PlexPlaybackRegistry.Load.None, registry.LoadFor(item));

        registry.Record("server", playing, TimeSpan.FromSeconds(30));
        Assert.Equal(PlexPlaybackRegistry.Load.Other, registry.LoadFor(item)); // Not resolved yet.
        registry.RecordItems("server", [item], [cached]);
        Assert.Equal(PlexPlaybackRegistry.Load.OnlyItem, registry.LoadFor(item));
        Assert.Equal(PlexPlaybackRegistry.Load.Other, registry.LoadFor(cached));
        Assert.True(registry.IsCachedPlayback(cached));
        Assert.False(registry.IsCachedPlayback(item));

        registry.RecordItems("server", [], [cached]);
        Assert.Equal(PlexPlaybackRegistry.Load.None, registry.LoadFor(item));

        registry.Record("other", playing, TimeSpan.FromSeconds(30));
        registry.RecordItems("other", null);
        Assert.Equal(PlexPlaybackRegistry.Load.Other, registry.LoadFor(item));
        registry.Record("other", [], TimeSpan.FromSeconds(30));
        Assert.Equal(PlexPlaybackRegistry.Load.None, registry.LoadFor(item));

        registry.Record("server", playing, TimeSpan.FromSeconds(30));
        Assert.Equal(PlexPlaybackRegistry.Load.Other, registry.LoadFor(item)); // A fresh poll clears the previous resolution.
        Assert.False(registry.IsCachedPlayback(cached));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
