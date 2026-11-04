# Legacy uncertain repair actions

Migration now retains unresolved `ArrRegrabRequests` and library-link removal
journal records as immutable, digest-keyed evidence in an additive SQLite or
PostgreSQL table. Unknown fields survive. Failed or skipped requests remain held:
a failed response does not prove the remote operation failed. Explicit terminal
replacement rows need no new uncertainty hold. Missing item bindings impose a
global replacement hold. Malformed, incomplete or oversized evidence aborts import
instead of being silently discarded. Source rows and journals remain read-only.

The guard also runs for configurations an earlier Rust build marked imported.
Manual replacement, automatic replacement, unowned deletion, tracked Arr mutation
and hidden-source cleanup honor it. Safe checks and local PAR2 repair can continue.
No legacy record becomes a fresh mutation-ready regrab.

A bounded background lane observes recorded movie or episode IDs through the
unique matching configured Arr instance. It requires an exact old file ID, media
kind and path, a positive different current file ID, and unchanged configuration.
Only GET requests occur. Related removal evidence needs a unique request identity,
exact recorded link path and compatible item binding. Ambiguity remains held.
Observation checkpoints advance even for unavailable or unknown targets so one
record cannot monopolize the lane.

Replacement proof is retained separately from original evidence. Proven unbound
records stop imposing a global hold. Bound original IDs remain protected even
after observation: proof of an external replacement is not permission to repair a
new generation at the old ID or delete a source without current-generation and
reader admission checks. Automatic legacy source retirement and an operator
reconciliation UI remain follow-up work.

Back up `/config` before upgrading; the additive migration applies at startup.
There are no new setup-wizard options or automatic activation of old worker jobs.

Validation: formatting and all-target Clippy passed. The workspace suite passed
616 tests on SQLite and local PostgreSQL, with six external/native-fixture cases
ignored. Generated source-journal tests cover restart, torn records, uncertain
requests and unbound holds. A real loopback HTTP Arr fixture proves observation
issues no external mutations, retains original-ID protection and resolves only
the corresponding global hold. These fixtures are not live Arr or production
acceptance.

The three genuine 1.x C# writer/import/cache fixture cases also passed separately,
as did both privileged kernel FUSE cases, including process-challenge reads.
The two cache suites must run sequentially because they deliberately acquire the
same exclusive source-fixture lease. An initial parallel run correctly refused
that shared lease; the sequential rerun passed. Legacy cache bytes are copied into
separate Rust storage; the runtimes never share a writable cache root.
