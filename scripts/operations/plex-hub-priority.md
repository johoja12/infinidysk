# Prioritize sealed Plex import batches

The full-library API defaults to contiguous numerical batch order. An operator
can explicitly send `allowOutOfOrder: true` to `/api/migration/nzbdav/full/connect`
for remaining batches of an established recovery master. Batch zero must still
establish the master. Package identity, master counts, session transitions and
acknowledgement of every previously registered batch remain required. The API
never renumbers packages or changes their manifests.

The operation is advanced-only: it adds no persisted application setting, so it
does not affect the setup wizard or its version.

`plex_hub_priority.py` builds a read-only snapshot using the configured, enabled
Plex servers and their connected account tokens. Tokens stay in memory and are
sent in headers. It matches Plex media parts to selected library-relative paths
through the configured local path mappings; it does not match by title.

Priority tiers are:

1. Continue Watching, On Deck and Next Up.
2. Trending and recommendations.
3. Other visible video hubs, including recently added and favourites.

The snapshot takes the first 50 items in each hub. Series hubs contribute the
first five unwatched episodes in season/episode order. Within each tier, batches
with more matched files come first; original batch index breaks ties. Every
batch remains in the schedule, including batches with no hub matches.

Generate a new snapshot on the migration host (Docker config access required):

```sh
sudo python3 /opt/infinidysk-migration/plex_hub_priority.py \
  --batches "$BATCHES" --master "$MASTER" --batch-count 92 \
  --library-root /mnt/plex --output "$REPORTS/plex-hub-priority-candidate.json"
```

Review `order` (zero-based indices), `batches[].matches`, `countsByTier`, and hub
evidence before activating. The generator refuses to overwrite any snapshot;
publication is atomic. Keep timestamped snapshots for audit and rollback.

Deploy the backend supporting the opt-in flag and install the helper beside the
runner. At a safe batch boundary, install the runner and publish the reviewed
candidate as `import-reports/plex-hub-priority.json`. Preserve any existing local
runner fixes. Do not interrupt link validation, promotion or acknowledgement to
install a scheduler.

The runner reads the active snapshot at every batch boundary. Any registered,
unacknowledged batch always finishes first. Subsequent selection follows the
snapshot, excludes acknowledged batches, and verifies the selected manifest
hash before staging. A restart resumes by registered batch identity, rather
than by the number of completed batches. Invalid schedules, multiple active
batches and missing schedules after sparse registration fail safely.

Hub changes do not silently rewrite the schedule. Generate and review a new
snapshot, then atomically replace the active file at a batch boundary to refresh
priorities. Sealed package checksums and the master digest stay unchanged.
After any batch has run out of order, retain a complete schedule until all
batches are acknowledged; removing it is not a rollback to numerical order.

### Worker limits

The runner caps the import queue at ten items and uses two submission workers.
Link validation and validation retries use four workers (`validate-links
--workers 4`), with the existing 64 KiB read limit and 20-second read timeout.
The standalone validator defaults to one worker and accepts 1–16; its report
retains journal order and is published only after every applied link finishes.
Source-missing and source-replaced entries remain excluded from validation.

Install the matching migration tool before the updated runner. Resume from the
existing batch ledger and journals; do not reconnect or resubmit a completed
batch to change its worker count. Submission workers take effect when the next
batch is connected. Preserve recorded replacement exclusions when restarting.
