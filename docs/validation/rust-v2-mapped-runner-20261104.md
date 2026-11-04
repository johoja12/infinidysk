# Sealed-package submission runner

`scripts/operations/rust-import-runner.py` verifies existing 1.x mapped-package schema
1/2 exports without changing the sealed manifest. It checks the independently retained
manifest digest, complete checksum set, payload sizes/hashes, success/exclusion reports,
unique release/leaf/link bindings and safe relative paths. Parent directories and files
are opened without following symlinks. Payloads are checked again before dispatch.

The default command verifies local bytes only:

```sh
python3 scripts/operations/rust-import-runner.py \
  --package /private/package --manifest-digest <retained-manifest-sha256>
```

Dispatch additionally requires `--run`, an explicit API URL, exact fork source commit,
owner-only API-key file and a separate private runner journal. The source identity is
checked against the native Rust API. Connection secrets do not enter arguments or
journals. Request redirects are refused. Submission options use a bounded header.

The append-only, exclusive journal is fsynced before dispatch and after accepted
readback. Restart observes the stable intent before any write. Changed payloads,
options, server plans or accepted job IDs are refused. A previous dispatch with no
readable acceptance is held for explicit recovery; it is never blindly replayed.
Torn journals are preserved and refused. Import concurrency is bounded to 1–8 slots.

**This increment implements verified submission and recovery, not completed migration
or library promotion.** Completed/failed/unknown-history jobs remain recorded. The
runner reports `acknowledged: false`; exact imported-member validation, independent
validation scheduling, batch acknowledgements, priority schedules and guarded link
promotion are subsequent steps. It performs no Arr calls or library-link mutations.

Five generated-package tests pass, including an explicitly simulated lost-response
client across journal restart. These are Python fixture results. Rust queue/database
and actual HTTP behavior are qualified separately in
[migration admission](rust-v2-migration-admission-20261104.md). No live Plex/provider,
production filesystem mutation, merge, publication or deployment is claimed.

## Native CLI rehearsal

Qualified aggregate source: `8438e25f`. The clean-source native example was copied to
an immutable path before execution. Binary SHA256:
`497dc89cc917771c64a95f92f318455ff5be6620595163241b8f8b2279b77e55`.
The actual Python CLI submitted a paused generated NZB through the native API,
preserved an explicit legacy job name, restarted the backend behind a stable local
fixture origin, and read back the same job. Removing that job left one durable
acceptance receipt with unknown history. Subsequent CLI passes created no new job.
The runner correctly refused a changed server origin in an earlier rehearsal.

This is actual native API/SQLite/restart evidence using a generated mapped-package
fixture. It proves submission recovery, not original C# export generation, member
correlation, payload/provider acceptance or library-link promotion. The runner
continued to report `acknowledged: false`. Private receipt:
`/tmp/infinidysk-rust-browser/migration-admission-validation.json`.
