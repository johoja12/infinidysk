# Move one cache folder to v2

New v2 storage must be separate from every v1 root and from the metadata
catalogues. Back up settings and catalogues before changing storage. Keep the
original blocks until independently verified coverage and pins are accounted for.

Configure one enabled, writable v2 folder with a fresh identity and bounded quota.
Keep all existing v1 read mappings. Select **Write new blocks to v2 storage**
(`native_cache.prefer_native_writes`) and restart. The default is false, preserving
existing installations. With it enabled, new fills use v2 only; configured
compatible-write destinations remain available for a rollback but are not used
as an automatic fallback. V1 cache hits continue working. No writes are duplicated.

The setting is rejected without a writable enabled v2 folder. Storage-full or
unavailable v2 writes decline the fill; playback can still use the source. This
feature does not alter provider concurrency, playback read-ahead or memory limits.

## Copy existing blocks

The authenticated `POST /api/v1/native-cache/import-legacy` endpoint accepts:

```json
{
  "use_running": true,
  "source_folder": "configured-folder-id",
  "cursor": {"after_key": "", "offset": -1}
}
```

The running mode shares the server's already owned legacy catalogue. Do not send
offline paths or folders with it. It requires one registered source folder and
at least one writable v2 destination. It pauses during open playback sessions.
Each request examines at most 16 entries/blocks and returns a resumable cursor,
processed byte count, rejected counts, completion and paused state. Replayed
blocks may contribute to the processed count again; verify actual coverage
rather than using this count to authorize deletion. It checks
only the selected source folder before pagination.

Both original imported generations and exact `rust-v2:<revision>` generations
are accepted after comparison with current authoritative item metadata. Stale
or unmapped generations are reported as rejected. Blocks are checksum-validated
and published through the native writer, regardless of playback write policy.
Only verified blocks in v2 satisfy replay checks; a v1 read-through hit does not
count as migration. Source pins become destination pins. Sparse coverage remains
sparse. The migration never contacts providers or deletes source payloads.
Minimum-size, quota and recovery restrictions still apply to destination writes;
a block that cannot be retained pauses the copy rather than advancing its cursor.

Run the bounded operator command with credentials supplied through a protected
environment, not as command-line arguments:

```sh
python3 scripts/operations/migrate-cache-v2.py \
  --url http://127.0.0.1:18080 \
  --source-folder configured-folder-id \
  --checkpoint scrap/cache-migration/selected-folder.json \
  --max-turns 100
```

Set `INFINIDYSK_API_KEY` securely before running. The script persists its source,
server, cursor and cumulative report with atomic replacement and file/directory
fsync. It locks the checkpoint against concurrent local runners. Stop with Ctrl-C
and rerun the same command to resume. An interrupted request may have copied
blocks beyond the saved cursor; replay verifies v2 and safely reuses them.
The checkpoint contains no media names or credentials. Exit 0 means complete,
2 means paused/incomplete/rejected, and 1 means a request/local error. There is no
automatic retry of uncertain writes and no automatic source deletion. Rejected
checkpoints require operator review before continuation. Do not change their
source/server identity to reuse a cursor against different data.

Start with a small bounded run. Verify byte hashes, coverage and pins, then repeat
with the same checkpoint. Completion means the cursor scan finished; rejected
counts must also be zero before claiming full migration. Review uncopied and
unowned entries, source changes and storage failures separately. Keep old read
mappings while they contain any unique valid blocks. Do not treat the report as
authorization to delete an entire shared legacy catalogue.

Offline import remains supported with `metadata_path` and `folders` instead of
`use_running`; the source must still be offline/checkpointed and exclusively
owned. Optional `source_folder` also restricts offline imports.

## Rollback

Keep both formats' data and read mappings. Unselect the v2 write preference,
restore compatible writes if necessary, and restart. Do not overwrite a live
catalogue with an older backup: v2 blocks created during the pilot must remain
readable. No format conversion occurs in place.
