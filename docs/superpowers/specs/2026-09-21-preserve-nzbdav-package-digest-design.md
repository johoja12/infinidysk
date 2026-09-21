# Preserve NzbDav Package Digest Design

## Problem

`NzbDavScanRunner` records the verified package digest in each scanned
`MigrationReleaseFile.Flags` document. When a submission completes,
`MigrationProvenanceService.RecordCompletedAsync` replaces that entire document
with correlation evidence. Full-library reconciliation then correctly refuses to
operate because it can no longer prove that the package matches the scanned
ledger.

This occurred in the completed 30-file production canary. The request failed
before writing links or resubmitting data. The frozen package still validates,
and its checksum-inventory digest matches the digest retained independently in
both the original connect response and correlation report.

## Application repair

For NzbDav sources, completion will merge correlation evidence into the existing
flags document instead of replacing it. The resulting document retains
`packageSha256` and stores the latest evidence under a `correlation` property.
Malformed or structurally unexpected existing flags fail closed; completion must
not silently discard provenance.

Reconciliation remains strict. It continues to require the exact package digest
on every selected ledger row and rejects missing or mismatched values. No
projection-based fallback, filename matching, or automatic historical backfill
is added.

Focused tests will prove that:

- completion preserves the scanned package digest;
- correlation evidence remains available;
- reconciliation still rejects missing and mismatched digests; and
- reconciliation remains idempotent and performs no queue, history, or DAV
  writes.

## Production recovery

After the fix is merged and deployed, the existing canary ledger receives a
one-time, operator-controlled repair:

1. Back up `usenet-migration.db` and verify the backup checksum.
2. Revalidate the frozen package and independently retained digest evidence.
3. In one SQLite transaction, update only the 30 selected canary
   `ReleaseFiles.Flags` documents, preserving their existing evidence and adding
   the proven `packageSha256` value.
4. Verify exactly 30 rows changed and every selected row now carries the expected
   digest.
5. Retry reconciliation and require 30 selected, 30 exact, zero ambiguous, zero
   unmatched, zero submitted, and unchanged queue/history/DAV counts.

Any count, digest, JSON, or transaction mismatch aborts before reconciliation.
The repair does not modify the package, imported DAV data, `/mnt/plex`, or
`/mnt/plex2`.

## Rollback and boundaries

The application rollback is the prior pinned image. The ledger rollback is the
checksummed pre-repair SQLite copy, used only if the narrowly scoped transaction
cannot be validated. Plex and Arr remain disconnected from `/mnt/plex2`, and the
legacy NzbDav deployment remains untouched.
