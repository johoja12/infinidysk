# Durable Rust migration admission

Migration submission binds a sealed manifest digest and source-release identity to
one stable intent. Acceptance binds exact decompressed NZB bytes, original filename,
explicit legacy job name, category, priority and post-processing options. A receipt,
queue row and NZB blob are committed in one transaction. Concurrent/repeated calls
return the original job. Changed bytes/options are refused. A separately queued SAB
job is not acknowledged under a migration's identity/category.

Submission options travel as bounded base64 JSON in `X-Migration-Options`, keeping
legacy filenames (which may contain archive passwords) out of URL access logs.
The authenticated admin API accepts raw NZB bytes at
`POST /api/v1/migration/submissions` and reads the stable intent at
`GET /api/v1/migration/submissions/{intent}`. Invalid/rejected migration submissions
create no accepted receipt. A successful receipt outlives queue/history pruning.
Missing history is explicitly unknown and never permission to submit again.
Acceptance proves queue admission, not exact imported-member identity or authority
to rewrite a library link. Completed imports still require source validation.

SQLite and PostgreSQL migration `0023_migration_submissions` is additive.
**Back up `/config` before upgrading**; migrations apply at startup. No runtime
setting, setup-wizard control or legacy production endpoint is changed.

## Validation

604 locked workspace tests passed, zero failed and six default ignores, with actual
SQLite and disposable PostgreSQL 17. Formatting and warnings-denied all-target Clippy
passed. API generation and the production TypeScript/Vite build passed.
Shared database exercises prove repeat admission and transaction rollback:
a rejected queue insertion leaves no acceptance receipt or extra blob.

Queue integration tests prove concurrent calls, changed bytes/categories, completed
jobs, restart, history pruning, invalid NZBs and a separate SAB job in another
category. An actual HTTP test leaves the first response unread, recovers the stable
receipt through GET, repeats POST without a second job, refuses changed options and
retains acceptance after queue/history disappearance.

These tests use generated files and loopback providers. Mapped-member correlation,
link promotion, legacy repair-journal activation, complete batch orchestration and
production acceptance remain separate work. No merge, image publication or deployment
is claimed.

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
