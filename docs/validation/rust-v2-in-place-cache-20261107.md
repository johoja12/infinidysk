# Read-only in-place v1 native cache compatibility

This change adds an explicit read-only adapter to the v2 fork. It preserves
the original cache payloads, catalogue, manifests, ownership markers and repair
ledger. No production data, configuration or services were used or changed.

## Runtime behavior

Playback and warming use the same `Cache::get` lookup. On a Rust-cache miss,
the adapter loads the item's preserved import provenance, matches the current
Rust content fingerprint and size, independently derives the original generation
from the offline repair ledger, and resolves the exact legacy catalogue key.
Only committed blocks with matching geometry, manifest and SHA256 are served.
Sparse and chunked layouts are supported across up to 64 registered folders.
Corrupt, missing or mismatched data never becomes a hit and is never repaired,
rewritten or removed by this adapter.

The original folder identities and volume markers must match. All configured
legacy owner locks remain held for the adapter's lifetime. Blocking physical
reads retain their buffer and volume lock even if the caller times out or
cancels. Shutdown releases ownership only when physical reads finish.

Both original SQLite databases must be stopped and checkpointed: nonempty WALs
are rejected. In-place connections are immutable and read-only, avoiding SQLite
sidecar writes. Do not modify these databases externally while v2 owns the cache.
The existing copy importer retains its separate behavior and is not used here.

Existing entries below the configured minimum new-fill size remain readable.
With no Rust write folders, cache misses are served from the provider for normal
playback without retaining new bytes. Warming consumes existing verified blocks;
an uncached block defers its job with an explicit writable-folder reason and
spends no fill/provider budget. New writes to the v1 format are not implemented.

## Explicit configuration

Keep migrated legacy paths inactive until handover is ready. Stop every v1
cache writer, checkpoint both original ledgers, preserve the import provenance,
and bind the original metadata/payload roots read-only at their registered paths.
Use a **separate private writable metadata directory for v2**. Example fragment:

```json
{
  "native_cache": {
    "enabled": true,
    "metadata_path": "/config/native-cache-v2",
    "folders": [],
    "legacy": {
      "metadata_path": "/config/native-cache-metadata",
      "folders": [
        { "id": "original-folder-id", "path": "/cache/original-root" }
      ]
    }
  }
}
```

Use the exact original folder IDs, paths, physical mounts and matching ledgers.
This is a configuration example, not a production execution instruction. It
requires restart. Startup fails if explicit handover cannot acquire ownership
or validate the source. Rust destinations overlapping legacy metadata or payload
roots are rejected before creation, including existing symlink aliases. Managed
scan folders cannot overlap the configured legacy roots.

`stats.legacy_hits` counts blocks served by the adapter; shared cache hit counts
and playback native-cache byte accounting also include them. The native file
browser, coverage, byte totals, eviction and pin operations still describe the
Rust catalogue. They do not index or mutate v1 entries. Legacy physical reads
are not included in the Rust payload I/O counters. Do not use zero Rust byte
totals as evidence that the legacy cache is empty.

## Qualification

Automated checks cover the actual application bridge, shared VFS delivery and
warming queue with no providers: sparse/chunked files in two folders, full reads,
cross-block seeks, restart, byte/allocation preservation, source provenance,
corruption, owner exclusion/release and rejection of uncheckpointed databases.
The native-store tests also gate a queued physical read to verify that caller
cancellation does not release its buffer or original ownership prematurely.

An additional fixture-dependent application test imports metadata produced by
the original C# writer and serves its sparse, chunked, stored-member, encrypted
and partial-cache payloads in place through both delivery readers. It also checks
warming reuse and partial-cache deferral. It copies source metadata for import;
cached media remains at its original path. This test requires the generated
`INFINIDYSK_LEGACY_CACHE_FIXTURE` receipt and runs separately from the default suite.

## Executed validation

Qualified application source: the commit containing this report, based on
`8469f3ed4db0d249918e494ce3decd18e6c21b82`. No Rust application source changed between
the final Clippy/workspace build and the separate C# fixture run.

| Check | Result |
| --- | --- |
| `cargo fmt --all --check` | Passed. |
| `cargo clippy --workspace --all-targets --locked -- -D warnings` | Passed. |
| `cargo test --workspace --locked` | 790 passed, zero failed, seven default ignores. Includes eight new default compatibility/fault checks. |
| Original C# in-place application test | Passed separately: five original writer layouts, both delivery readers, full/seek reads, warming reuse and partial-cache deferral, unchanged source inventory and zero Rust payload copies. |
| `npm run api` and `npm run build` with Node 26 | Passed; generated API types include the opt-in configuration and legacy-hit counter. |
| Local document links and `git diff --check` | Passed. |

The separately executed fixture case was selected from the workspace-built test
binary with `--ignored --exact
native_cache::legacy_tests::actual_csharp_payloads_serve_in_place_after_import_without_providers`
and the generated `INFINIDYSK_LEGACY_CACHE_FIXTURE` receipt. Its binary SHA256,
command logs and JSON summary are retained under
`scrap/upstream-sync-20261107/legacy-read-*`. The seven default ignores include
this case; the other six were not explicitly rerun for this change.

This is local qualification, not actual production NAS, kernel mount, live
Plex/Arr/provider acceptance or an exact-image production rehearsal. Keep
automatic repair/retirement and library mutators disabled during the initial
read-only production rehearsal. Legacy coverage/retention management and
compatible writes require further qualification before full production parity.
