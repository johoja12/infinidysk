The Plex migration runner checks the live `/mnt/plex` links before starting or
resuming each scanned batch. An untouched pending NZB is excluded when every
selected library file already points under `/mnt/remote/infinidysk/`. Regular
files, missing links, legacy targets, and similarly named directories are not
assumed to be InfiniDysk links. Link targets need not be mounted for this check.

Mixed NZBs are imported for their remaining files; existing InfiniDysk links
are preserved by the link-application guards. An unsupported or changed link
stops the runner for review rather than replacing it.

Install `existing_id_imports.py` beside the runner. The check requires the exact
scanned or paused NzbDav package and only removes pending submissions with zero
attempts, no job ID, and no conflicting provenance. It backs up the SQLite
ledger with the online backup API and records exclusions through the existing
scan-exclusion mechanism without changing the sealed package. Reports live in
`import-reports/batch-NNNN/already-infinidysk.json`; recorded skipped links are
rechecked before resuming, processing, and acknowledging a batch.

Updating the scripts does not start the service. Keep the import paused until
resuming is requested. Test the exclusion logic with:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s scripts/operations -p 'test_existing_id_imports.py'
```
