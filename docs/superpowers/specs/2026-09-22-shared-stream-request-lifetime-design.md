# Shared-stream request lifetime fix

**Issue:** [#23](https://github.com/johoja12/infinidysk/issues/23)
**Date:** 2026-09-22

## Problem

Native-cache and shared-stream source opening can be deferred until after the originating WebDAV request has completed. `BaseStoreStreamFile` already publishes the selected `DavItem` to `HttpContext.Items` before it creates the native-cache wrapper. The concrete NZB, RAR, and multipart source openers repeat that write when the deferred source is finally opened.

When a shared-stream pump outlives its request, the repeated write touches a disposed ASP.NET feature collection and throws `ObjectDisposedException: IFeatureCollection has been disposed`. Production diagnostics reproduced this for both direct NZB and multipart items. The client received the requested bounded bytes, but the background pump failed and native cache could fall back.

## Chosen design

Request attribution remains a request-layer responsibility:

1. `BaseStoreStreamFile.OpenFinalStreamAsync` publishes `DavItem` synchronously while the request is alive.
2. Concrete NZB, RAR, and multipart `GetStreamAsync` implementations only open media sources; they never read or write `HttpContext`.
3. `GetDetachedReadableStreamAsync` populates `DetachedStreamLease.DavItem` from the immutable `DavItem` property rather than rereading `Context.Items` after an awaited source open.
4. Existing response-path attribution stays unchanged for metrics, session tracking, and handlers that inspect `HttpContext.Items`.

This is preferred over cloning `HttpContext` for background work or passing a new request snapshot through every stream factory. Both alternatives preserve unnecessary request coupling and expand the change surface.

## Tests

Use test-driven development:

- Add a direct NZB regression that defers source opening, disposes the original request feature collection, and proves source opening does not access it.
- Add the equivalent multipart regression.
- Extend the base lifecycle test to prove the detached lease receives the selected `DavItem` without consulting disposed request state.
- Run focused WebDAV/shared-stream/native-cache tests, then rely on PR CI for the broader covered lanes.

The regression must fail with the current implementation specifically because the disposed feature collection is accessed, then pass after the minimal production change.

## Error handling and invariants

- Do not catch or suppress `ObjectDisposedException`; remove the invalid request access at its source.
- Preserve cancellation, stream ownership, native-cache admission, shared-stream lifecycle, and response cleanup behavior.
- Do not change configuration, database schema, setup wizard behavior, WebDAV routes, or cache modes.

## Delivery and production verification

1. Commit the focused fix on `fix/shared-stream-request-lifetime` and open a PR to `johoja12/infinidysk`.
2. Merge only after the required quality gate and substantive CI lanes succeed; the user's `do 1-5` instruction explicitly authorizes this exact merge.
3. Deploy the exact merge commit to the parallel InfiniDysk stack on nuc-1 using the documented deployment procedure, preserving rollback state and verifying image revision, health, mounts, and restart counts.
4. Select six previously unread files, balanced between direct and RAR/multipart representations, without clearing caches.
5. Capture native-cache counters and bounded logs before and after the run. Repeat the direct-versus-rclone-mount first/repeat probes.

Acceptance requires byte-correct reads, no timeouts, no new shared-pump disposal failures, and no attributable increase in native-cache I/O timeouts. Fallback deltas must be explained from logs; unexplained fallback growth blocks Plex testing. Plex, Arr, `/mnt/plex`, and Plex registration of `/mnt/plex2` remain out of scope.

## Setup-wizard impact

None. The fix changes internal stream lifetime ownership only and introduces no user-facing configuration.
