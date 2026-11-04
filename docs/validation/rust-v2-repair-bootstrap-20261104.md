# Repair startup and replacement coordination

Exact Rust source: `f9fae905044cb11eb1722ab74881a11366ad084b`.
Fork 1.x remains read-only at `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

Existing authoritative repairs are attached before serving requests, independent of
whether future attempts are enabled. Stable config-directory storage is independent
of payload retention. Startup reads the main/native/decoded lineage witnesses before
writing any binding. Missing or mismatched authoritative data stops activation.

Publication and destructive health actions share a barrier. Pending publication
blocks replacement. Fully covered recorded damage requests fresh urgent verification;
partial coverage does not claim health. Provider I/O cannot publish a health verdict
across a changed repair generation.

## Validation

- Full workspace: **504 passed, zero failed, six ignored**, using SQLite and an actual
  task-owned PostgreSQL 17 instance.
- Workspace formatting and all-target warnings-denied Clippy: passed.
- Five startup tests and four health integration tests passed. The barrier test was
  corrected to accept either pending readback or immediate verification, depending
  on the publication timing; both must preserve the source and reach a fresh check.
- Three original unmodified C# writer interoperability tests: passed separately.
- Two actual privileged kernel FUSE tests: passed separately.

No live provider/Plex acceptance, performance measurement or deployment was performed.
The optional local NZB corpus remains unavailable. Automatic PAR2 discovery, complete
article assembly, durable attempt scheduling/outcomes and the remaining fork policy
work are still pending. This is an incomplete draft migration, not a cutover.

Main database migration 0017 and local catalogue tables are additive. Back up the
config directory and external cache metadata before upgrading. This increment adds
no persisted setting, API shape or setup-wizard branch.
