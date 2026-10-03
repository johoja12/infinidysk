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

## Failed imports are regrabbed, not cleaned up

`plex-import-runner.py` no longer runs `cleanup-failed-imports` for failed batch
imports (nor replays it for earlier batches at startup); that tool deleted the
legacy NzbDav item. InfiniDysk now queues a Sonarr/Radarr regrab for imports that
fail as damaged or missing articles: it removes only the broken library symlink
(journaled in `/config/regrab/library-link-removals.jsonl`) and the Arr file record,
then requests a search. The runner records the regrab state (`regrab-queued`,
`regrab-requested`, …) with the Arr target in each batch's `not-imported.csv`.
Links whose new InfiniDysk copy fails bounded-read validation are handled the same
way: the runner asks InfiniDysk to regrab the old library link
(`POST /api/arr-regrab` with `source: "migration"`) instead of running
`cleanup-validation-failures`, and records each outcome in
`validation-regrab-outcomes.json` and `validation-not-imported.csv`. The runner no
longer prepares private cleanup credentials (legacy API key, Arr config) at startup.
