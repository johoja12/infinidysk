# Verified Plex source-session correlation

Qualified source: `aaff9828`. Library playback rows and file details now show selected
Plex sessions reported for the exact current imported source revision. These are
source activity labels; an HTTP/FUSE caller does not identify a remote Plex viewer.
All matching players/viewers remain visible instead of choosing one.

The planner first validates the Plex machine and independently resolves the exact
viewer credential. Current session file and viewer-scoped metadata file must match.
Missing session paths stay unresolved; they no longer inherit metadata's first file.
History/next-episode candidates carry no live-session context. Display fields and
matching session lists are bounded. Legacy saved snapshots default to empty context.

Correlation uses a complete, fresh, non-restored planner snapshot, its configuration
scope, the current source revision and the read time window. Evidence expires after
at most two minutes even with a longer refresh setting. Future timestamps, outages,
source changes, metadata uncertainty and unrelated older reads leave attribution
unknown. Configuration is fenced again before publishing correlated rows. The private
scope fence is excluded from JSON/OpenAPI. Read/status handlers make no Plex requests
and perform current-source metadata work for at most 32 sources. They read no media.

## Validation

- **593 locked workspace tests passed, zero failed, six default ignores**, on actual
  SQLite and disposable PostgreSQL 17. Formatting, warnings-denied all-target Clippy,
  API generation and production TypeScript/Vite build passed.
- Real loopback Plex transport/planner tests validate two players on one verified
  source, viewer-credential refusal, missing/mismatched session file paths, complete
  snapshots, outages and policy/source changes.
- Deterministic row-correlation tests retain multiple candidate viewers and reject
  wrong revisions, stale/future evidence and unrelated older bursts.
- Native binary SHA256:
  `5688048536097ce9c2bb0df810bf4948c8183014198aae784d222633ba91c219`.
  Actual native generated playback and both API views return empty Plex attribution
  when no verified Plex session exists. The private scope is absent from JSON.
- Separately, a **UI-only response fixture** supplied two reported Plex sessions to
  the real file-details page. Chromium rendered both names and the explicit source
  activity caption at 390 px without overflow or page errors. This presentation
  fixture is not live Plex/provider acceptance or native credential verification.

Private artifacts: `/tmp/rust-fork-playback-correlation-*` and
`/tmp/infinidysk-rust-browser/playback-correlation-validation.json` plus mobile capture.
Original C# cache and kernel-FUSE compatibility were last checked separately at
`0880d5b7`; this presentation/planner increment does not repeat those checks.
No configuration option or setup-wizard version change is introduced. Detailed
warming rate/repair attribution and migration workflows remain open. No image
publication, production cutover, merge or deployment is claimed.
