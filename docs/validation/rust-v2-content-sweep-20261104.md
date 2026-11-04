# Resumable sampled-content health sweep — 2026-11-04

Rust base: `e97300e9` on `feat/rust-v2-fork-migration`.
Specification remains fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

The new Health operation persists an atomic candidate snapshot, versioned state,
revision, cursor, category, per-run limit, counters and bounded recent findings in
SQLite/PostgreSQL. Pause interrupts in-flight samples; resume keeps the snapshot.
Source deletion remains countable without resetting progress. This samples decoded
content across imported media, rather than verifying every byte.

Each file has a first/last plus even 1 percent BODY sample (8–32 when available),
known-hole coverage capped at 64 probes, concurrency two and a 25-second deadline.
Imported part/range geometry is checked within the provider cascade before a BODY
can reach its caller or cache sink. The 1.x missing-total/obfuscated-total tolerances
are retained. Cross-sample size disagreements cannot yield a healthy result.
Partial volumes without trusted offsets stay unverified.

Read/explicit repair priority precedes media analysis; after four priority jobs a
maintenance slot rotates across sweep, analysis and routine checks. Foreground
activity and new urgent requests interrupt an admitted sample without advancing its
cursor. Sweep journal errors leave other health lanes available. Metadata CAS and
a short publication gate preserve source identity and newer playback reports;
provider waits occur outside the gate. Destructive policy remains in the ordinary
health coordinator, including confirmation, settings, native copies and reader holds.

Validation evidence:

- Focused DB, health and NNTP suites passed on SQLite and actual loopback PostgreSQL.
  The later workspace run includes all six new health regressions and the new NNTP
  sink regression passing. Shared DB assertions exercise snapshot/CAS behavior on
  both backends.
- Regressions cover restart and per-run quotas; stale controls; literal categories;
  source deletion/replacement; unavailable versus missing providers; foreground and
  pause cancellation; 64-probe bounds; priority versus analysis; and a playback fault
  arriving while a regular check is in flight.
- All-target workspace Clippy passed before the final API-auth assertions were added.
  Generated API schema, TypeScript/Vite UI build and native canary build passed.
- Actual Rust app/API + Chromium + loopback NNTP: Start, Resume and Pause controls,
  stale-revision rejection and durable snapshot accounting passed. Four generated
  candidates yielded two sampled healthy, one unreadable metadata result and one
  source deleted after snapshot creation. A throttled source kept the final pause
  case in flight. No browser errors or horizontal overflow at 390 px; screenshot
  visually inspected. This is generated local acceptance, not live provider evidence.
- Browser receipt: `/tmp/infinidysk-rust-browser/content-sweep-validation.json`.
  Canary SHA-256: `a23cd98c8e199fe918364ccdc5e1d759dabcd60a9a964586cbb2b7fba41925f2`.
- The first full workspace run stopped at the pre-existing queue timing test
  `a_stalled_job_does_not_hold_up_the_next_one`: **401 passed, one failed, five
  ignored** before remaining packages ran. Its unchanged two-second assertion has
  failed in earlier migration checkpoints too. This is not reported as a green full
  run; a separate test-fixture correction and complete qualification follow.

No new persistent setting or guided-setup input is added. The operation belongs on
Health after installation. Migration 0016 is additive: **back up `/config` before
upgrading**. No production service, 1.x checkout or writable cache root was changed.
PAR2 execution, other listed migration groups and live acceptance remain separate.
