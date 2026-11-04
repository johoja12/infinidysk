# New-ID import member proof

The authenticated `/api/v1/migration/reimport-proof` endpoint handles members that
receive a new Rust ID when a sealed NZB is submitted again. It requires the durable
submission intent derived from that package and source release, exact accepted XML
hash, completed job history, and membership in the job's recorded source subtree.
Missing history stays pending; it never authorizes resubmission.

The offline 1.x source is reconverted without providers and compared against the
current Rust metadata's content identity: article order, decoded geometry, member
ranges and decryption parameters. The original C# article identity must match the
sealed member. A held read handle, current metadata fences and final job/subtree
readback bind the proof to the current source revision. Unknown geometry or a
changed source is refused. A shared bounded mapping lane remains occupied if the
caller times out while physical parsing is still running.

This does not certify healthy media bytes. A newly allocated ID receives no legacy
cache provenance or permission to adopt old cache blocks. The preserved-ID migration
and verified copy of the 1.x cache remain the preferred continuity path.

The canary CLI accepts `--reimport-map PRIVATE_JSON --reimport-map-digest SHA256`.
The map is owner-only, version 1, bound to `packageDigest` and `serverCommit`, and
contains `bindings` with `legacyItem`, `currentItem`, `submissionIntent` and
`sourceReleaseId`. Every selected member needs one unique binding. The retained
digest is the SHA256 of the exact file bytes. Each binding is checked against fresh
completed acceptance and server member proof before samples or canary links are
accepted. Job/directory identity also participates in resume checks. Mapping changes
require a new plan. Conditional offline rollback still needs only the saved plan
and its owned canary directory.

Validation: formatting and all-target Clippy passed; 617 workspace tests passed on
SQLite and local PostgreSQL, with six fixture-dependent cases ignored. API generation and the UI build also
passed. The native
HTTP integration test uses real admission and proof endpoints with generated
MemoryPack source data and a seeded completed job. It covers exact source/job/member
binding and rejects another release, another subtree and a changed identity. It is
not a real provider import. The offline conversion test separately covers new IDs,
source metadata changes and absence of legacy cache adoption.

Fourteen Python tests passed. New-ID canary plan/apply/resume/offline-rollback tests
use generated mount files and simulated proof/acceptance responses. Pending, failed,
missing-history and wrong-payload receipts cannot proceed to member correlation.
These are distinct from native FUSE or real provider qualification. Full automatic
candidate discovery and multi-batch operational acknowledgement remain open.
