# Typed fork policy migration

The C# importer now translates supported Smart Prefetch policy values and additional
read-only library roots. Legacy configuration is read through the existing read-only
C# configuration adapter, including its environment overlay.

- Preserves movie/television switches, history/realtime policies, lookback windows,
  episode thresholds, full/minimum warming, head/tail byte counts, finish-watched
  settings and bounded daily allowances.
- Preserves the original binary-MB head/tail byte count, then displays decimal MB
  through Rust's normal UI. Fractional confidence percentages round upward so the
  migration does not widen prediction eligibility.
- Requires explicit enablement of warming and Plex prediction refresh after import.
  Legacy names and global source lists cannot establish the new verified viewer
  identities. Their selections require review; they do not silently become an
  unrestricted viewer selection.
- Imports additional read-only scan roots only when Rust's path/overlap validation
  accepts the entire list. It never grants conversion or cleanup authority to those
  roots. An unresolved Plex library server selection becomes an explicit empty
  selection, with an actionable migration note.
- Leaves native cache metadata and payload paths inactive. Configure separate Rust
  storage, then run the verified legacy cache-copy workflow. This preserves the
  existing 1.x writer and rollback boundary.
- Rejects malformed values, ambiguous property casing and overflowing numeric
  values without partially installing warming settings. Unsupported legacy fields
  produce migration notes. Use `fork-export` before import to preserve their exact
  private values and operational journals; this translator does not create an
  archival export automatically.

Three focused tests include the real read-only C# SQLite configuration adapter.
The fixture proves that its source database file is byte-identical after policy
translation and that legacy cache paths never become active Rust settings.

PostgreSQL-source translation, journal activation, mapped recovery packages,
operational runner compatibility and full migration rehearsal remain open. This is
not a claim of complete M01–M04 parity or production readiness.

## Qualified follow-up

Source checkpoint `b4986192`: **572 workspace tests passed, zero failed, six default
ignores**, including actual PostgreSQL 17 and SQLite. Formatting, warnings-denied
all-target workspace Clippy, API generation and the production UI build passed.
The three original C# writer interoperability cases and two privileged kernel-FUSE
cases also passed when explicitly enabled. The optional external corpus remains
unavailable and was not run.

The source-bound viewer restriction now has an explicit limit switch. With the
switch on, an empty list permits no viewers. All history, realtime and selected-source
planner paths use the same check. Unresolved legacy viewer/source restrictions turn
this switch on with an empty selection; enabling global warming alone cannot widen
that selection. Existing Rust settings retain their prior default behavior. This is
an advanced control, with generic environment ownership and validation handled by
the existing settings infrastructure; it adds no new-install setup requirement.

A rebuilt native canary passed actual generated-source WebDAV/cache/API checks and
mobile Chromium acceptance. It recorded 8,388,608 synced write bytes and 4,194,304
verified block-read bytes for an 8 MB complete read followed by a 1 KB range request.
The difference illustrates physical block I/O versus bytes sent to the player.
Both active and queued storage work returned to zero. The explicit empty viewer
selection persisted through a real settings save and reload. The cache diagnostics
page fit a 390 px viewport and produced no page errors. No live provider/Plex,
production deployment or general performance percentage is claimed.
