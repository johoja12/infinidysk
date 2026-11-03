# Rust migration branch upstream sync — 2026-11-04

Fork branch before sync: `3a0ada46e914fe570f4204d3d565819ce029878f`.
Previous upstream baseline: `b16e071ed60815d34cdb9a84ccc60695bceb7fea`.
New upstream tip: `27ce0448d4b6a27f4ffe990dadf2b195c29d72e5`.
The full 14-commit upstream history is merged into the existing migration branch.

## Integration

Upstream adds parent-app attribution for helper reads; confined STRM/library writes;
legacy import path protection, progress and large-metadata streaming; transactional
Arr ownership protection; bounded archive/import work; large PAR2 indexing; and NNTP
failover on truncated articles. No database migration is added by this sync.

Six files had textual conflicts. Resolutions preserve both sides:

- Database tests run both completed-pointer removal and concurrent Arr adoption checks
  on SQLite/PostgreSQL.
- Settings probing stays outside the configuration write lock, then rechecks the target
  before save. Fork Plex credential invalidation and warming reconciliation remain.
- Subtitle materialization drops the analysis reader and uses upstream's confined
  destination handling.
- The fork's multipart content-identity field uses the streaming reader's callback
  lifetime contract. Duplicate dependency declarations are removed.
- Legacy conversion keeps the fork's source/revision provenance while adopting the
  safe streaming blob opener and parser. A hashing reader covers the entire original
  compressed input without retaining a second metadata byte buffer. Failed reads do
  not publish provenance. The original C# writer checks prove unchanged generations
  and verified cache reuse after a full import.

## Validation

The frozen merged source passes **452 workspace tests, zero failed and six ignored**,
with actual disposable PostgreSQL 17 and SQLite. Formatting and all-target workspace
Clippy pass. The two new provenance regressions cover a short parser read followed by
complete input hashing, and an input failure preventing a source generation.

All three original C# writer interoperability tests pass separately, sequentially,
using the immutable generated `20261103c` fixture. They cover full import/shared VFS
without providers, multipart/repair generation identity and partial coverage retention.
The upstream upgrade-gate browser fixture passes with no page errors. Its status/auth
responses are simulated; it covers cached status failure/recovery and migration stages,
not a real installation upgrade.

API/schema generation and the TypeScript/Vite production build pass. Both privileged
kernel FUSE tests also pass separately, including reads through native cache. The
remaining ignored NZB sample case still requires an external local fixture.
No live Plex/provider, decoder, performance, release image or deployment is claimed.
The original C# checkout stays unchanged. The existing migration PR remains a draft;
its previously documented port gaps are not closed by an upstream merge.

An initial all-target compile caught old `&Vec<u8>` decoder calls in fork fixture code;
these now pass slices to the new Read-based decoder. An unused test import was removed
before qualification. No failed test was skipped or deadline relaxed.
