# Process-bound mount validation and separate canary links

The authenticated migration mount-probe issues one unlisted, expiring 16-byte
challenge per running VFS. A mount from another process, even against the same
database, cannot satisfy it. Challenges are neither media nor persistent items.

Validation: formatting, all-target Clippy, 612 workspace tests passed (six ignored),
including SQLite and local PostgreSQL. Both privileged kernel FUSE tests passed
and read the process challenge through the mounted filesystem. API generation and
UI build passed. These checks do not certify production providers or deployment.

`scripts/operations/rust-migration-canary.py` binds a sealed mapped package to
preserved legacy IDs, current source proofs, process challenges, exact original
symlink witnesses and bounded head/middle/tail samples. Default execution only
writes a private plan and validation journal. `--apply` creates symlinks in a
separate owned canary directory; it never modifies the original library or
acknowledges cutover. Repeat execution resumes only an identical plan.

Use `--rollback --plan-file PRIVATE_PLAN --canary-library OWNED_CANARY` for
conditional offline rollback. It requires neither the backend nor source mount;
changed or foreign links are preserved. Empty owned directories and the ownership
marker remain. Keep the plan and journals for recovery.

Eleven Python tests passed using generated filesystem data and simulated API
proofs, including actual bounded reader subprocesses, resume, rollback, source
changes and timeout handling. This coordinator evidence is distinct from native
FUSE qualification and does not certify a production migration. Sampled reads do
not certify whole-file health. Canary directories are private to the invoking
user; application access requires a deliberate permissions review. Generic NZB
reimports with newly allocated IDs still need explicit member reconciliation.
