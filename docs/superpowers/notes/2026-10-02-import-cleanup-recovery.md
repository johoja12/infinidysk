# Temporary migration cleanup recovery

Failed-import cleanup now saves the exact Arr instance, file ID, path, and media IDs
before deleting the legacy source. Legacy deletion and Arr deletion use at most four
attempts with 2/4/6 second backoff. Every retry rechecks authoritative ownership;
an absent legacy mapping, Dav item, and source link or absent Arr file confirms the
completed action. A replacement file or changed owner stops cleanup.

Arr search has a separate durable boundary: save the command IDs already present
and the start time before submitting once. A returned command ID confirms acceptance.
An uncertain response or restart rechecks commands by exact media IDs, name, timestamp,
and absence from the saved baseline. It never blindly repeats a search POST. Missing,
ambiguous, or failed command evidence stops for reconciliation. Cancellation propagates.

Older `arr_cleanup_started` journals without saved identities still require manual
reconciliation. Preserve a backup and collect authoritative Arr history for the exact
source path and media identities, prove deletion and absence of a replacement, and
establish whether search was attempted before enriching the journal. Do not infer an
identity from a title, filename, byte size, or absence alone. A proven deletion before
search can be recorded with `arrState.stage=search_ready`; do not mark it completed.

This is one-time migration tooling under `tools/NzbDavMigration`. It adds no permanent
application configuration, setup wizard behavior, or application dependency.
