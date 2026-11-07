# v1-format cache writes from fork v2

Prepared 2026-11-07. Source: the commit containing this report, based on
`71a1ab5f`. This is local implementation and qualification, not a production
cutover. Production cache payloads and services were not changed.

Tracking: [compatible writes #236](https://github.com/johoja12/infinidysk/issues/236),
[fault qualification #239](https://github.com/johoja12/infinidysk/issues/239),
[NAS handover #238](https://github.com/johoja12/infinidysk/issues/238),
[production migration #237](https://github.com/johoja12/infinidysk/issues/237).

## Behavior and storage contract

The shared playback and Smart Prefetch cache can now fill the original v1 store.
Read-only remains the default. Opt-in writes use its existing SQLite catalogue,
folder identities, sparse/chunked payload layout, manifests and newline JSON
SHA256 journals. No cache conversion, payload copy, relocation, automatic eviction
or payload deletion is performed. Committed matching blocks are verified and
reused; committed different bytes are refused. Confirmed corrupt coverage is
invalidated under a shared reader/writer lock before a fresh fill can heal it.
Unknown I/O failures do not invalidate coverage.

Imported unchanged sources retain their original generation from import provenance
and the immutable original repair ledger. New v2 sources use `rust-v2:` plus the
current content revision; changed revisions cannot adopt older bytes. Format
readability does not import new v2 jobs, source identities or library changes back
into the v1 application database. Post-write application rollback still needs
reconciliation.

All original folder ownership locks are exclusive and retained through physical
I/O, including caller cancellation. Current device/inode, volume marker and original
owner inode are revalidated. Descriptor-relative no-follow operations refuse
symlinks, hard-linked leaves and cross-device entries. Metadata must be on local
storage. Only the current v1 schema/accounting triggers are accepted; the writer
never migrates that schema.

Configured folders have independent priority, quota and free-space reserve. New
entries may choose another qualified folder; an existing entry remains in its
original folder. In-flight reservations also account for shared filesystem devices.
No sums of free capacity across exports should be assumed. Allocation accounting
is conservative and can overcount interrupted/healed fills until an explicitly
qualified reconciliation. Each folder has at most one outstanding capacity probe;
NAS I/O runs outside the catalogue-wide placement gate. Playback and warming retain
the shared buffer admission and existing minimum-size policy for new fills. Reads
of already cached smaller files remain eligible.

## Durable publication and recovery

The publication order is reservation in SQLite, complete synced manifest, payload
write and sync, journal append and sync, then committed block coverage in SQLite
WAL with FULL synchronous mode. Every directory link is synced before publication, including pre-existing
directories: visibility after an earlier failed flush is not proof of durability.
Payload/journal names are also flushed when already present, and recovery
re-establishes the directory chain before committing coverage. These additional
barriers apply to fills and need actual NAS performance measurement. Parallel blocks
share manifest preparation and serialize journal appends. No payload file is opened
with truncation. Per-block reader/writer guards prevent a stale failed read from
invalidating a newly healed block. Caller cancellation cannot release the buffer
permit or original ownership while the owned task is still performing I/O.

A retry can reconcile a matching durable journal record by verifying its actual
bytes and hash, without reserving space twice. Incomplete journal tails and writes
without sufficient durable proof remain held in `PendingBytes`; they are neither
advertised as coverage nor overwritten speculatively. The bounded uncertainty set
stops further writes if exhausted. Existing committed blocks remain readable.

For a held entry, pause new fills, retain its evidence, and investigate under
exclusive ownership. A qualified original v1 scanner can reconstruct coverage from
manifests/journals. Do not clear pending accounting, edit journals, replace owner
files or delete payloads as an automatic repair. No unattended recovery of every
possible partial-write state is claimed.

Combined native/legacy coverage now participates in retained-copy verification.
Complete contributions are pinned before physical verification. Read-only unpinned
legacy coverage cannot prove durable retention; an existing pin can. Writable
legacy health protection uses v1 pin value 2 and never clears manual pin value 1.
Unresolved publications and uncertain storage keep verification uncertain and keep
protection. Conclusive missing/corrupt coverage can release only the automatic pin.

## Explicit configuration

Example fragment only; use the actual registered IDs and original absolute paths,
reviewed quota/reserve values and a separate writable v2 metadata directory:

```json
{
  "native_cache": {
    "enabled": true,
    "metadata_path": "/config/native-cache-v2",
    "folders": [],
    "legacy": {
      "metadata_path": "/config/native-cache-metadata",
      "folders": [{ "id": "original-folder-id", "path": "/cache/original-root" }],
      "write": {
        "chunk_mb": 8,
        "folders": [{
          "id": "original-folder-id",
          "priority": 10,
          "quota_bytes": 1000000000000,
          "min_free_bytes": 100000000000
        }]
      }
    }
  }
}
```

`chunk_mb: 0` selects sparse storage; 4–256 selects chunks rounded to the block
size. Existing entries retain their stored geometry. Omit `write` for read-only
handover. Writes require original metadata and selected payload roots mounted
read/write after v1 has stopped. The original repair ledger remains immutable. In write mode every owner file
must be openable read/write; use the same-inode owner binding below for an
otherwise read-only root. Unselected payload roots can remain read-only.
Do not run two application owners or background scanners concurrently.

If an NFS configuration requires a writable descriptor for an exclusive ownership
lock while payloads are bound read-only, a folder may set `owner_lock_path` to a
read/write bind of its exact original `.infinidysk-owner` file. The bind must resolve
to the same device/inode with one link; an unrelated replacement is refused. Its
contents are never written. This binding is optional: same-host probes on the four
current production NFS mounts accepted exclusive locks from read-only descriptors.
The actual container binding still needs rehearsal.

`stats.legacy_written_bytes` counts newly published compatible bytes;
`stats.legacy_hits` counts successful legacy hits. Shared hits/playback byte
accounting include legacy reads. The native file browser, native entry/byte totals
and manual management actions still describe the Rust catalogue. Legacy physical
I/O is not included in Rust payload I/O counters. Zero native totals do not mean
the old cache is empty. No complete legacy administration UI parity is claimed.

## Validation and limits

| Check | Result |
| --- | --- |
| `cargo fmt --all --check` | Passed. |
| `cargo clippy --workspace --all-targets --locked -- -D warnings` | Passed. |
| `RUST_TEST_THREADS=4 cargo test --workspace --locked` | 803 passed, zero failed, nine default ignores. |
| Final legacy-adapter integration suite | Six passed, zero failed, one fixture-dependent ignore. |
| Native cache unit suite, including writer fault/publication and recovery | 15 passed; two explicit helpers ignored. |
| Application compatibility/retention tests | 10 passed; one receipt-dependent case ignored. |
| Original C# reading Rust sparse/chunked writes and reconstructing journals | Passed separately, two generated layouts. C# harness built with zero warnings/errors. |
| Original C# fixture through Rust application in place | Passed separately, five layouts, providerless playback/seek/warming and unchanged source inventory. |
| API generation and TypeScript/Vite build, Node 26 | Passed. |
| Local document links and `git diff --check` | Passed. |

Raw logs and generated receipts are retained in `scrap/upstream-sync-20261107/compatible-*`.

The first unrestricted workspace run and an isolated rerun hit the existing
recovery-pagination fixture's one-second storage deadline while hashing four
blocks in a debug build on this shared host. That functional fixture now has a
10-second I/O budget; the production default and explicit timeout tests are
unchanged. Both failed logs are retained. A later full run exposed the old cancellation
test's assumption that a blocking-pool barrier implied completion of the whole
read. The test now waits with a bound for the buffer permit and original owner
lock, including the owned task's metadata work. Its pre-completion exclusion
assertions remain. The final workspace rerun uses
`RUST_TEST_THREADS=4`; concurrent publication remains exercised inside the tests.

The local fault suite uses disposable generated directories only. It covers sparse
and chunked parallel publication, duplicate/conflicting blocks, restart, quota and
reserve refusal, multiple folders, no relocation, short payload/journal writes,
injected ENOSPC/EIO, twelve error boundaries, seven subprocess-death boundaries,
request cancellation, owner replacement, symlink refusal and changed volumes.
Application cases cover provider-backed missing/new-source warming, providerless
restart reuse, automatic warming re-admission without refetch, retained-copy pins,
corruption/healing, pending-write uncertainty and mixed native/legacy coverage.

The C# verifier links unmodified fork v1 cache sources at `9c104b59`. It reads
Rust-written sparse/chunked bytes and, in generated fixtures only, removes catalogue
coverage then reconstructs it using the original scanner and Rust journals. The
reverse-direction original C# fixture test remains a separate check.

Read-only production preflight found four NFS roots and the required current
catalogue columns/accounting triggers. Tiny generated files outside the cache roots
qualified same-host read-only/read-write exclusive locking and exclusion of a
second process on all four mounts. All probe files were removed. These are
primitive checks, not a real cache lease handover, container mount validation,
NAS failover/remount test, live cache write or matched throughput comparison.

The remaining execution packet is
[the NAS rehearsal runbook](../rust-v2-cache-handover-runbook.md). Production
readiness also depends on the broader
[production migration plan](../rust-v2-production-migration-plan.md), including
feature gaps, snapshots, live integrations and post-write rollback reconciliation.
