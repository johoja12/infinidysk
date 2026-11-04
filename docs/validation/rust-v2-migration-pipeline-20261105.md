# Resumable canary migration pipeline

`scripts/operations/rust-migration-batches.py` verifies a privately retained SHA256
of a complete priority schedule and the exact selected sealed package on every
admission. It accepts the 1.x frozen schedule shape, bootstraps batch zero, and then
preserves the frozen order across restarts. It retains separate child acceptance,
member-map, validation and link journals. Legacy journals and packages are inputs;
they are never rewritten.

There is one importing batch and one validating batch. The importing batch keeps
advancing bounded release passes while the independent validator is busy. A second
completed import waits for the validation slot; a held validator cannot admit a
third batch. Root checkpoints precede dispatch and resume through child acceptance
readback, not blind retries.

Validation discovers the unique exact new-ID member through completed-job/source
proofs, reads the expiring process challenge through the actual mount, samples the
member, and creates links only in a separate owned per-batch canary directory.
Conditional rollback preserves foreign or changed links. Canary validation is a
local disposition, never production cutover acknowledgement or whole-file health.

Missing, failed, ambiguous, changed and unavailable sources stay held. To dispose
of a reviewed terminal failure, use `--run --skip-batch INDEX --disposition-file
PRIVATE_REASON` with the same plan and roots. Fresh exact acceptance readback must
show every release terminal; active or uncertain dispatch cannot release a slot.
Any existing owned canary plan is conditionally rolled back first. The journal
retains the reason digest and terminal receipts as `skipped_reviewed`, separate
from `canary_validated`.

Use `rust-migration-priority.py` to create a new frozen schedule from a complete,
current Rust Plex prediction snapshot. It ranks exact legacy item IDs, suppresses
unresolved predictions, records snapshot/source provenance and seals each batch
manifest digest. It never refreshes that snapshot during resume. An already
retained 1.x priority schedule can instead be supplied directly.

All commands default to verification or explicit output generation. Running a
pipeline requires `--run`, an exact fork commit, an explicit API origin, a private
API key, an offline source copy, original library, native canary mount and separate
private state directory. The runtime must have read-only access to the same source
path. Limits are explicit: 1,000 releases/selected members per batch, 100 candidate
files per imported release by default (up to 1,000), and bounded phase deadlines.
Re-export smaller sealed batches or review limits instead of bypassing evidence.

Systemd templates exist for standalone acceptance and the batch pipeline. Neither
has been installed or enabled here. All four templates passed `systemd-analyze
verify`. A dedicated CI lane now runs the standard-library Python tests and unit
syntax verification.

Validation so far: 617 Rust workspace tests passed on SQLite/local PostgreSQL,
formatting and all-target Clippy passed, and 27 Python tests passed. The new Rust
member-mismatch classification lets discovery distinguish a definitive different
candidate from unavailable source evidence. Scheduler tests cover restart, sparse
priority order, two-slot admission, import progress during validation, explicit
terminal dispositions and changed plans. These Python phases use generated data
and simulated child/API results; native end-to-end pipeline qualification is a
separate gate.
