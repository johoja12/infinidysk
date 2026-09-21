# Plex Scoped Source Discovery Design

Issue: [#16](https://github.com/johoja12/infinidysk/issues/16)

## Problem

Smart Prefetch discovers Plex libraries and global hubs, but every library shows zero hubs and zero collections. InfiniDysk requests scoped hubs from `/library/sections/{libraryId}/hubs`; the production Plex server returns `404` for that route and returns the expected hubs from `/hubs/sections/{libraryId}` instead.

Collections are available from `/library/sections/{libraryId}/collections`, but the current source request fetches collections and hubs as one operation. The later hub failure therefore discards the successful collection response and produces a stale empty snapshot.

## Scope

This fix changes only Plex catalogue discovery and its focused tests. It does not change persisted Smart Prefetch settings, source identities, preview behavior, frontend contracts, setup-wizard behavior, or global `/hubs` discovery.

## Design

`PlexApiClient` will expose separate bounded operations for collections and hubs:

- Scoped collections use `/library/sections/{libraryId}/collections`.
- Scoped hubs use `/hubs/sections/{libraryId}`.
- Global hubs continue to use `/hubs`.

`PlexCatalogueService.GetSourcesAsync` will retain its existing public return type and controller contract. For a scoped library it will obtain independently cached collection and hub snapshots, then merge their data. For a global request it will obtain only the global hub snapshot.

The independent cache keys prevent a failure in one Plex resource from replacing successful data from the other resource. Existing snapshot bounds, refresh coalescing, one-minute freshness, and thirty-second failure backoff continue to apply to each resource.

## Snapshot Aggregation

For scoped requests, the aggregate snapshot will:

- concatenate collection data followed by hub data;
- set `IsStale` when either component is stale;
- retain each successful component even when the other component fails;
- combine distinct non-empty component errors into one safe error string;
- report the newest non-null `LastSuccess` timestamp from its components.

The API continues returning HTTP 200 with snapshot status for upstream catalogue failures, matching existing behavior. Credentials, upstream response bodies, and tokens remain excluded from errors and logs.

## Tests

Focused tests will prove:

1. Scoped hub discovery requests `/hubs/sections/{libraryId}`, not the unsupported legacy route.
2. Scoped source discovery returns both collections and hubs when both Plex requests succeed.
3. A failed scoped hub request produces a stale aggregate containing successful collections and a safe error.
4. A failed collection request produces a stale aggregate containing successful scoped hubs and a safe error.
5. Global source discovery still requests `/hubs` and does not make a collection request.

The regression test will be run red before production code changes, then green after the minimal implementation. No setup-wizard update is required because the change adds no configuration option and changes no installation flow.
