# Warden, Watchdog, and Preflight

## Watchdog

Playback failover when a release cannot be served: try parallel candidates within a time budget, optional stall failover, and size-variant retention modes.

Enable and tune under [Watchdog settings](../configuration/watchdog.md).

### Replacement outcomes

The Watchdog also records Sonarr/Radarr queue imports. When recent Arr history confirms that the same episode or movie was subsequently imported from a different download, its failed request shows **Replacement imported**, the replacement release, and import time. The original failure stays in the attempt details, and the request counts as resolved. This records an import outcome, not a playback or current-file health check.

**Recovery unconfirmed** means no replacement import was confirmed from the available history; it does not prove the episode is still missing. Checks use exact download and media IDs within one Arr instance, never title similarity. History checks are limited to the latest 500 events per enabled instance and cached for one minute; unavailable instances, older events, and ambiguous multi-episode downloads can remain unconfirmed.

## Preflight

Background warm-up of top search results before the user clicks (**off / light / standard / full**). Warms state used on the hot path so the first play is faster.

[Preflight settings](../configuration/preflight.md)

## Warden

Portable **dead-release** fingerprint ledger. Filter search hits that match known-dead releases; sync remote sources with quorum; optional private GitHub backup of your local list.

Fingerprints only — no credentials. [Warden settings](../configuration/warden.md)

Together with [Watchtower](watchtower.md), these form the readiness and safety layer around search and playback.
