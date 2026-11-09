# Playback backfill validation

Qualified source: `0880d5b7` (product behavior in `52733635`). Together with
`e05d42c6`, this completes the local implementation of the P04 capacity/backfill
increment. Controlled performance and live Plex/provider acceptance remain separate.

Foreground native readers now report exact uncached ranges after a handle has
actually served four integrity blocks (16,777,216 bytes, about 17 MB). Seek offsets,
repeated polling and short probes do not qualify. Analysis and warming do not create
foreground hints. Ranges align to integrity blocks and merge only within the same
item/revision. The handoff holds at most 256 ranges, with up to 64 disjoint ranges
per unqualified handle; overflow is bounded and the queue's drop counter is visible.
A two-second debounce lets ordinary asynchronous cache commits finish first.

The producer rechecks current source identity, size and coverage, then saves a
priority-100 Backfill job before acknowledging the hint. A coalescing nonce prevents
an older acknowledgement from losing a newer range. Storage failure leaves the
hint pending. Confirmed unhealthy sources stay with repair. Recovery mode holds
backfill admission and execution. Own-file range jobs precede that file's full warm
while cached lead is short; foreground requests still preempt source work.

Backfill has its own policy family. Plex/finish-watched cleanup cannot cancel it,
and disabling warming cancels only its automatic jobs, preserving manual requests.
Accepted jobs use the existing durable progress/history, source-revision checks,
strict executor and daily fill/wire budgets. Pre-admission memory hints can be lost
on process failure; they are not reported as saved jobs. Cancelled intents are not
silently revived. Completed/expired work can reopen when a qualified new read finds
its coverage absent. No new user-facing configuration or setup-wizard branch is
introduced; the existing opt-in warming switch controls execution.

## Validation

- **591 locked workspace tests passed, zero failed, six default ignores**, with
  actual SQLite and disposable PostgreSQL 17. Formatting, warnings-denied all-target
  workspace Clippy, API generation and production TypeScript/Vite build passed.
- Generated NNTP/VFS integration proves buffer pressure leaves actually served
  ranges uncached; short far-seek probes create no hints. Accepted backfill survives
  queue reload, wins over the same file's full warm and remains independent of
  other policy cleanup. A changed source is not admitted. A closed database does
  not acknowledge the pending hint.
- **Three original C# writer interoperability cases passed separately**, including
  verified legacy cache copying through import and provider-free VFS reads.
  **Two privileged kernel-FUSE cases passed separately**, including native cache.
  These five cases are not included in the 591 default-test count. The optional
  external NZB corpus remains unavailable.
- Rebuilt native binary SHA256:
  `d402128b7ffd007185392df1cddb7671483e2fc78b7eda8095f0ef96bd3fb62e`.
  The actual process served bytes 1 through 16,777,216 of a generated 25,165,824-byte
  file. That left two partial cache blocks: `[0,4194304)` and
  `[16777216,20971520)`. Automatic backfill verified and stored all 8,388,608 missing
  bytes. After stopping/restarting the native process, the completed job remained
  saved. Removing providers still allowed the qualified range and a cross-block
  seek to match the original playback bytes exactly. The 390 px Chromium UI showed
  the Backfill job with no page errors or overflow.

Private artifacts: `/tmp/rust-fork-backfill-*`,
`/tmp/infinidysk-rust-browser/playback-backfill-validation.json` and its mobile capture.
The canary's `--large` option only creates generated local data; resume accepts its
known 8 MB or 25 MB shape. Resume deliberately disables new warming for inspection.
Pending-job restart proof comes from the database/VFS integration case; the native
process rehearsal proves completed-job persistence and cached range delivery.

The 1.x cache contract remains verified copying into separate Rust storage, with
source/revision proof. No shared writable legacy cache, live provider/Plex test,
performance percentage, release image, production cutover, merge or deployment is
claimed. The full fork port still needs the remaining migration and correlation work.
