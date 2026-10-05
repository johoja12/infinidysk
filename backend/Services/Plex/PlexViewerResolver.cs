namespace NzbWebDAV.Services.Plex;

public sealed record PlexViewerResolution(PlexServer? Server, string Status, string? Message);

/// <summary>Per-pass credential verification; names never authorize watched-state access.</summary>
public sealed class PlexViewerResolver(PlexApiClient api, IReadOnlyList<PlexAccount> accounts)
{
    private readonly Dictionary<string, (PlexAccount Account, IReadOnlyList<PlexDiscoveredServer> Resources)> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlexViewerResolution> _results = new(StringComparer.Ordinal);

    public async Task<PlexViewerResolution> ResolveAsync(PlexServer server, string? user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user)) return new(null, "missing-identity", "Plex did not identify the viewer. Retry after Plex history updates.");
        if (server.AccountId == user && !accounts.Any(account => account.Id == user))
            return new(server, "verified", null);
        var key = server.Id + ":" + user;
        if (_results.TryGetValue(key, out var retained)) return retained;
        PlexViewerResolution result = new(null, "unconnected", "Connect this Plex or Home profile in Plex connections to verify watched status.");
        foreach (var account in accounts.Take(16))
        {
            var expected = account.Id == user || (user == "1" && account.Id == server.AccountId);
            try
            {
                if (!_verified.TryGetValue(account.Id, out var verified))
                {
                    var identity = await api.GetAccountAsync(account.Token, ct).ConfigureAwait(false);
                    // A replaced credential must not inherit the previous account's authorization.
                    if (identity.Id != account.Id && identity.NumericId != account.Id)
                    {
                        if (expected || result.Status == "unconnected")
                            result = new(null, "identity-unresolved", "A connected account identity could not be verified. Review its Plex connection.");
                        continue;
                    }
                    verified = (identity, await api.DiscoverAsync(account.Token, ct).ConfigureAwait(false));
                    _verified[account.Id] = verified;
                }
                var resource = verified.Resources.FirstOrDefault(resource => resource.Id == server.Id);
                // PMS represents the owning account as local account 1. Ownership must be
                // asserted by this authenticated account's resource list, on this machine.
                var matches = user == "1" ? resource?.Owned == true
                    : user == verified.Account.Id || user == verified.Account.NumericId;
                if (!matches)
                {
                    if (expected && user == "1")
                        result = new(null, resource is null ? "no-server-access" : "identity-unresolved",
                            "The connected account could not be verified as this server’s owning profile. Review its Plex connection and server access.");
                    continue;
                }
                result = resource is null
                    ? new(null, "no-server-access", "This connected profile cannot access the Plex server. Restore its server access in Plex.")
                    : new(server with { AccountId = user, Token = resource.Token }, "verified", null);
                break;
            }
            catch (PlexRequestException exception)
            {
                if (expected) result = exception.AuthorizationFailed
                    ? new(null, "authorization-failed", "The connected profile's credentials were rejected. Reconnect it in Plex connections.")
                    : new(null, "verification-unavailable", "Plex account verification is unavailable. Retry the refresh when Plex is reachable.");
                else if (result.Status == "unconnected")
                    result = new(null, "verification-unavailable", "A connected account could not be checked. Retry before changing Plex connections.");
            }
        }
        _results[key] = result;
        return result;
    }
}
