using System.Net;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexCatalogueServiceTests
{
    [Fact]
    public async Task FailedRefresh_RetainsLastGoodSnapshotWithSafeErrorAndBackoff()
    {
        var failed = false;
        using var handler = new FakePlexHandler(_ => failed
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret") }
            : PlexApiClientTests.Xml("""<MediaContainer><Directory key="1" type="movie" title="Movies"/></MediaContainer>"""));
        var clock = new PlexTestClock();
        var catalogue = new PlexCatalogueService(new PlexApiClient(new HttpClient(handler), "installation"), clock);
        var good = await catalogue.GetLibrariesAsync(PlexApiClientTests.Server());
        Assert.False(good.IsStale);
        failed = true;
        clock.Now = clock.Now.AddMinutes(2);
        var stale = await catalogue.GetLibrariesAsync(PlexApiClientTests.Server(), forceRefresh: true);
        Assert.True(stale.IsStale);
        Assert.Equal("Movies", Assert.Single(stale.Data).Title);
        Assert.Equal(good.LastSuccess, stale.LastSuccess);
        Assert.DoesNotContain("secret", stale.Error!);
        await catalogue.GetLibrariesAsync(PlexApiClientTests.Server());
        Assert.Equal(2, handler.Requests.Count);
    }
}
