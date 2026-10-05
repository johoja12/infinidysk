using System.Net;
using System.Text.Json;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexViewerResolverTests
{
    [Theory]
    [InlineData("42", true, "machine", "verified")]
    [InlineData("1", true, "machine", "verified")]
    [InlineData("1", false, "machine", "identity-unresolved")]
    [InlineData("42", false, "other-machine", "no-server-access")]
    [InlineData("99", true, "machine", "unconnected")]
    public async Task ResolvesOnlyAuthenticatedNumericIdentityOrThisServersOwner(string historyId, bool owned, string machine, string status)
    {
        using var handler = new FakePlexHandler(request => Json(request.RequestUri!.AbsolutePath == "/api/v2/user"
            ? """{"uuid":"login-uuid","id":42,"username":"Same name"}"""
            : JsonSerializer.Serialize(new[] { new { clientIdentifier = machine, provides = "server", owned, accessToken = "viewer-server-token" } })));
        var resolver = new PlexViewerResolver(new(new HttpClient(handler), "test"), [new("login-uuid", "Same name", "viewer-token")]);
        var server = new PlexServer { Id = "machine", AccountId = "login-uuid", Token = "owner-token", Url = "http://plex.test" };
        var result = await resolver.ResolveAsync(server, historyId, CancellationToken.None);
        Assert.Equal(status, result.Status);
        if (status == "verified")
        {
            Assert.Equal(historyId, result.Server!.AccountId);
            Assert.Equal("viewer-server-token", result.Server.Token);
        }
        else Assert.Null(result.Server);
        var count = handler.Requests.Count;
        Assert.Equal(result, await resolver.ResolveAsync(server, historyId, CancellationToken.None));
        Assert.Equal(count, handler.Requests.Count);
    }

    [Fact]
    public async Task AReplacedTokenCannotAuthorizeThePreviouslySavedIdentity()
    {
        using var handler = new FakePlexHandler(_ => Json("""{"uuid":"different-person","id":42,"username":"Same name"}"""));
        var resolver = new PlexViewerResolver(new(new HttpClient(handler), "test"), [new("saved-person", "Same name", "token")]);
        var result = await resolver.ResolveAsync(new() { Id = "machine", AccountId = "saved-person" }, "saved-person", CancellationToken.None);
        Assert.Equal("identity-unresolved", result.Status);
        Assert.Null(result.Server);
        Assert.DoesNotContain("saved-person", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authorization-failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "verification-unavailable")]
    public async Task RejectedAndUnavailableConnectedAccountsHaveDifferentRecoveryActions(HttpStatusCode status, string expected)
    {
        using var handler = new FakePlexHandler(_ => new(status));
        var resolver = new PlexViewerResolver(new(new HttpClient(handler), "test"), [new("login", "Viewer", "secret-token")]);
        var result = await resolver.ResolveAsync(new() { Id = "machine" }, "login", CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("secret-token", result.Message);
        Assert.Null(result.Server);
    }

    [Fact]
    public async Task MissingAndUnconnectedViewersNeverBorrowTheServerOwnersWatchState()
    {
        using var handler = new FakePlexHandler(_ => throw new InvalidOperationException("No credentials should be queried."));
        var resolver = new PlexViewerResolver(new(new HttpClient(handler), "test"), []);
        var server = new PlexServer { Id = "machine", AccountId = "owner" };
        Assert.Equal("missing-identity", (await resolver.ResolveAsync(server, null, CancellationToken.None)).Status);
        Assert.Equal("unconnected", (await resolver.ResolveAsync(server, "another-user", CancellationToken.None)).Status);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
