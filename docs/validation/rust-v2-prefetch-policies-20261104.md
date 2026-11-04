# Rust Smart Prefetch policy validation

Source checkpoint: `0c66162e` on `feat/rust-v2-fork-migration`. This is a local
qualification receipt, not full fork parity, live Plex acceptance or a deployment.

## Behavior

- The warming transport reserves durable UTC-day response credits before provider
  requests. Actual application response bytes after TLS consume those credits,
  including responses drained after a caller cancels. Playback uses its own lane.
  Greeting/authentication and TCP/TLS buffering are outside this measurement.
  Unused reservations are conservatively lost across restarts and policy changes.
- SQLite and PostgreSQL persist the queue and immutable terminal history together.
  Deterministic outcome identities prevent duplicate history after uncertain commits.
  Readback fences unresolved writes before another mutation can proceed.
- Finish-watched jobs require two minutes of foreground serving, at least 67 MB
  served and the configured share of the source. Seek offsets do not count as
  watched bytes. Repeated polling does not count bytes twice. Plex and observed-read
  policies have separate reconciliation scopes.
- Minimum head/tail warming can supplement prediction windows. Hub, collection and
  library resources use exact configured server and verified viewer identities.
  Resources cannot supply arbitrary destinations or credentials. Limits select an
  ordered candidate prefix. Incomplete refreshes preserve the previous snapshot.
- Retained decoded memory can rebuild final-media blocks without sending provider
  commands or reserving wire credits. Revision/repair checks remain mandatory.
  Disk-only decoded article reuse is not added by this increment.
- The new controls are advanced-only and disabled by default. They do not belong on
  the new-install setup critical path. Existing installations must explicitly enable
  warming and the desired policies. There is no setup wizard version increase.

## Automated checks

`cargo fmt --all --check`, workspace all-target warnings-denied Clippy and the full
locked workspace suite passed: **562 passed, zero failed, six ignored**. The suite
included actual PostgreSQL 17 on a disposable loopback database as well as SQLite.
TypeScript API generation and the production Vite build passed.

The six default ignores are the three original C# writer interoperability cases,
two privileged kernel-FUSE cases, and the optional external NZB corpus. At the
preceding `c8b7b8ec` policy checkpoint the three C# cases and two FUSE cases passed
when explicitly enabled. They were not repeated for the later source-selector and
decoded-memory-only changes; those changes add no legacy storage format.

## Native local acceptance

The rebuilt native `fork_local` example, with the production UI embedded, used a
private database, FUSE mount, generated 8 MB source, loopback NNTP server and native
cache. Binary SHA-256:
`1c1a89da64376ed6e73a7413d1855b223a81cee676acf8f1d5f1688a1196bc27`.

Eight complete WebDAV reads over 105 seconds served verified source bytes. No
finish-watched job appeared early. After final-media eviction, a read at 125 seconds
qualified the source. The job rebuilt all 8,388,608 bytes from retained decoded
articles: **zero provider-response bytes, zero wire reservations**. Explicit retry
retained the previous attempt in history and used no provider bytes. Total elapsed
acceptance time was 130.337 seconds. These timings establish eligibility behavior;
they are not a controlled performance comparison.

Headless Chromium at 390 px rendered warming history without page errors or
horizontal page overflow. A separate browser check created and applied a library
source, verified persistence after reload, and discarded an additional draft hub.
Plex prediction refresh stayed disabled in this settings check; it made no external
Plex requests. Verified-viewer transport and source failure behavior were exercised
separately with loopback HTTP fixtures.

Private receipts and screenshots remain under `/tmp/infinidysk-rust-browser/`;
public documentation includes no credentials or media titles.

## Remaining acceptance

The full connection governor, urgent seek backfills, live Plex/provider behavior,
controlled cold-read/seek performance and broader library/migration acceptance
remain separate work. No production image was built or deployed for this receipt.
Back up `/config` before upgrading: the new wire budget and warming history tables
are additive migrations which apply at startup.
