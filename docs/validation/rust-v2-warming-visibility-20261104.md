# Warming rates, provenance and repair follow-up

Qualified product source: `641c2308`. Recent fill rates use monotonic time and durable
progress checkpoints. Provider-response rates include metered abandoned drains.
Per-run windows are bounded and reset on controls/retries; cached progress does not
count as provider traffic. A background sampler keeps status polling read-only.

Admission preserves dated, bounded prediction-owner and verified source-viewer
context. Old journals deserialize without attribution. Retry history retains each
run's original provenance. Source activity does not identify a remote viewer of an
individual HTTP/FUSE request.

Current repair follow-up is observed separately from immutable warming history.
The original source's completed regrab journal and exact current library pointer
must resolve to a different current source. A completed partial range cannot prove
whole-file warming. A complete current-revision, full-size warm after the repair
request can. This is historical completion evidence, not a guarantee against later
cache eviction. The bounded background observation performs no Arr/Plex/provider
requests and reads no media. Stale, incomplete and failed observations stay explicit.

## Validation

- 598 locked workspace tests passed, zero failed, six default ignores, with actual
  SQLite and disposable PostgreSQL 17. Formatting, all-target warnings-denied Clippy,
  API generation and production UI build passed.
- One earlier parallel test run deferred an automatic warming tick without recording
  the reason. Its assertion now preserves diagnostic state. The isolated test, five
  full main-library repeats (540 test executions) and the final full workspace run
  passed. No root cause is claimed for that single earlier failure.
- Native example built from the clean qualified source with embedded commit/repository
  identity, then copied to an immutable path before testing. Executed SHA256:
  `124a6046dbe7c75580e111fe061ad5e3408fb8ebe25e74b449e5df9610035a34`.
- Actual 25 MB generated-source warming produced positive fill and provider rates.
  A generated replacement pointer and explicitly seeded read-only completed-regrab
  journal resolved to the current source. A partial warm remained incomplete; a
  later whole current-revision warm was recognized. Original outcome bytes stayed
  unchanged. Median status latency was 3.64 ms (maximum 88.39 ms) in this fixture.
  These observations are not a comparative performance claim.
- Actual mobile repair follow-up rendered at 390 px without overflow or page errors.
  Separately labelled UI response fixtures proved prediction-owner and dated viewer
  labels. Native provenance persistence/retry and verified planner attribution are
  covered by Rust tests, not claimed as live Plex acceptance.

Private artifacts: `/tmp/infinidysk-rust-browser/warming-visibility-validation.json`,
mobile captures and `/tmp/rust-fork-warming-*`. Original C# cache and kernel FUSE
compatibility were last checked separately at `0880d5b7`. No live Arr/Plex/provider,
image publication, deployment or production cutover is claimed. No settings or setup
wizard version changes are introduced.
