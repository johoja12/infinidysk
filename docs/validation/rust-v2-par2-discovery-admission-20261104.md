# PAR2 discovery and guarded publication admission

Exact Rust source for full qualification: `16dbd67e`, including `dcb832f1`,
`8c2f00e5` and `caa9d839`. Documentation checkpoint: `b79eba7e`.
Fork 1.x stays read-only at `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

## Implemented

Original posted-file ranges are discovered from complete validated BODY responses.
Damaged sources bind through imported PAR2 descriptors and exact contiguous article
ID spans; healthy additional sources require full MD5, first-16-KB MD5 and length.
Names supply only probe hints. Single unknown ranges need exact neighboring/proven
whole-file boundaries; consecutive unknown ranges are refused. Nonuniform article
sizes survive discovery and complete-article reconstruction.

Bad-CRC copies can be reconstructed only when all providers are conclusively
unusable. If any provider was skipped or failed, corruption remains inconclusive.
Discovery has bounded memory, requests, decoded bytes, cancellation and timeout.
Remaining request/read allowance passes into the verified executor. These decoded
allowances do not measure provider wire traffic.

VFS sessions bind source metadata before creating readers. Publication admission
waits for every dependent playback item sharing affected articles, including opens
still loading metadata. New overlapping reads are refused before byte delivery;
unrelated old/new readers continue. Finished sessions retain weak source references.
A target mutation slot and last-close wakeup cover the publication reservation.

Checked ledger publication revalidates source identity after acquiring the mutation
barrier and before a write transaction or pending revision. Cancelled validation
cannot produce an ambiguous commit. Deterministic expected revisions support a
future durable worker publication manifest.

## Qualification

- Full workspace: **527 passed, zero failed, six ignored**, using SQLite and actual
  task-owned PostgreSQL 17.
- Workspace formatting and all-target warnings-denied Clippy: passed.
- Three original unmodified C# writer checks: passed separately.
- Two actual privileged kernel FUSE checks: passed separately.
- Focused discovery/executor + health/NNTP units: 41 passed.
- Focused publication/admission targets: 20 passed.

Tests cover partial imported source spans, forged anchors, memory preflight before
provider work, outages, cancellation/permit return, bad CRC recovery, consecutive
unknowns and a nonuniform missing 4,700-byte article at offset 1,500. Reader tests
cover dependent/unrelated files, unknown opens, target mutation exclusion, wakeups
and weak metadata retention. Publisher tests prove a queued source change is checked
before mutation and that cancellation releases its writer without changing epochs.

The optional local NZB corpus remains unavailable. There was no production/live
provider/Plex acceptance, performance measurement, deployment or cutover. Automatic
worker integration, settings and durable scheduling remain incomplete. The later
attempt-journal increment is qualified separately by its focused database tests;
it is not part of the 527-test source checkpoint above.

No persisted option, HTTP shape or setup-wizard branch is introduced by the qualified
source increment. Back up the config directory and external cache metadata before
upgrading additive database/catalogue changes elsewhere in this draft.
