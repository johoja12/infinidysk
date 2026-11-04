# Complete repair articles, retention fallback and health readback

Exact Rust source: `5603ca00`, following `972b3a55` (complete verified articles),
`710aaf83` (later cache attachment guards) and `d584c3e9` (retained-hash fallback).
Fork 1.x remains read-only at `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

## Implemented

- Whole-file-verified slices become complete bounded articles. Healthy portions of
  mixed articles are re-read and IFSC-checked. Source changes reject the whole batch.
- Assembly preserves the execution request/read allowance and accounts for retained
  slices, article buffers and metadata before allocation. Bounds: 1,024 articles,
  256 MB-scale payloads and the remaining configured RAM allowance.
- Intentional repair-payload eviction retains its revision and immutable SHA-256.
  Provider fallback must prove full body geometry/hash before delivery or caching.
  Progressive delivery is disabled. STAT proves the BODY too. Cancelled responses
  cannot poison the repaired cache namespace. I/O uncertainty remains unavailable.
- A late cache-folder attachment cannot introduce foreign repair history when no
  authoritative ledger is active. Its marker stays intact and its disk tier disabled.
- Durable conclusive health receipts bind exact source content and repaired article
  revisions. Stale source/request receipts are refused. A retained tombstone cannot
  reset confirmed absence into an endless readback loop across restart. New repair
  revisions require new checks. Existing native-copy and reader/Arr guards remain.
- Publication waits from health generation comparison through verdict/receipt writes.

## Qualification

- Full workspace: **514 passed, zero failed, six ignored**, with SQLite and actual
  task-owned PostgreSQL 17.
- Workspace formatting and all-target warnings-denied Clippy: passed.
- Focused database/health/cache run: **45 passed**.
- Three original unmodified C# writer interoperability tests: passed separately.
- Two actual privileged kernel FUSE tests: passed separately.

New tests publish reconstructed complete articles through the actual repair store,
read them through provider-free NNTP, exhaust the original assembly allowance,
switch providers after whole-file proof to reject changed healthy bytes, reject
wrong-original fallback before progress/sinks, validate abandoned responses, separate
provider absence from no providers, reject directory-I/O fallback, persist health
readback across restart and require another check for a new repair revision. A
controlled barrier test proves a newer publication cannot inherit an older verdict.

The optional local NZB corpus remains unavailable. No live provider/Plex acceptance,
performance percentage, production deployment or cutover is claimed. Automatic PAR2
source discovery, durable attempt scheduling/outcomes and the background worker are
still open, as are the remaining inventory rows for library, prefetch and migration.
This draft is not complete.

Migration 0018 is additive. Back up the config directory and external cache metadata
before upgrading. No setting, API shape or setup-wizard control is introduced.
