# Preserve NzbDav source names during migration

**Issue:** [#25](https://github.com/johoja12/infinidysk/issues/25)

## Problem

NzbDav export packages store each NZB under a collision-safe internal path such as
`payloads/<source-release-id>.nzb`. `NzbDavScanRunner` currently treats that
storage basename as the submitted NZB filename. Normal queue processing therefore
creates UUID history names and mount directories. When media deobfuscation needs
its release-name fallback, the UUID also becomes the media basename.

The Phase B canary demonstrates the impact: all 16 imported releases have UUID job
names and 14 of 34 imported file leaves have UUID media names. The authoritative
legacy history rows, library paths, and NZB subjects still contain the correct
names, so this is migration metadata loss rather than source corruption.

## Package and import contract

`NzbDavExportRelease` will carry the legacy history filename and job name as
optional additive fields. New exports must populate both fields from the matched
legacy history row. They remain independent of `PayloadPath`, whose only purpose
is locating immutable bytes inside the package.

Manifest validation will reject unsafe source filenames, blank job names, and a
source job name that disagrees with the normal queue naming transform. This keeps
the importer on the ordinary submission path while proving in advance that the
resulting mount directory will preserve the legacy job name.

The scanner will prefer validated source naming metadata. Existing schema-v1 and
schema-v2 packages that lack it remain readable through the current payload-name
fallback, but they are not eligible for the name-preserving acceptance gate and
must be regenerated before a production replacement import. No setup-wizard or
database migration is required.

## Data flow

1. The legacy exporter joins each selected release to its authoritative history
   filename and job name.
2. The package writer records those names on the release and includes them in the
   existing checksummed manifest.
3. The package reader validates the immutable package as before, including the new
   naming fields when present.
4. `NzbDavScanRunner` derives `QueueFileName` from the source filename and verifies
   that `NzbDavNaming.JobName` equals the recorded source job name.
5. `SubmissionWorkerPool` submits the resulting queue filename through the normal
   queue service. Normal deobfuscation can safely use the human-readable mount name
   as its fallback.

## Tests

Test-driven implementation will cover:

- package serialization and checksum preservation of source names;
- rejection of unsafe or inconsistent source naming metadata;
- scanner persistence of human-readable submit, queue, and job names;
- explicit fallback behavior for old packages;
- an end-to-end direct-media case whose first-segment filename requires the mount
  name fallback, proving the output leaf is human-readable rather than UUID-based.

## Phase B replacement procedure

The destructive operation is confined to the parallel Phase B canary. Legacy
NzbDav, `/mnt/plex`, Plex, Arr, and unrelated InfiniDysk releases are excluded.

After the fix is merged by a human and the exact merged image is deployed:

1. Reconcile the live set back to the 16 migration submission IDs, 16 history
   rows, 16 download directories, 34 file leaves, and 30 `/mnt/plex2` symlinks.
2. Refuse cleanup if migration work is active, a target is outside the dedicated
   `migration-*` categories, `/mnt/plex2` contains a non-symlink, or a symlink is
   not one of the current canary paths.
3. Delete the exact completed history IDs through the supported SAB history API
   with `del_completed_files=1`; wait for mounted-content and blob cleanup, then
   prove the exact rows, directories, leaves, and native-cache entries are absent.
4. Delete every validated `/mnt/plex2` canary symlink and its now-empty parent
   directories. Delete the obsolete Phase B input package, plan, apply journal,
   evidence, and migration provenance, as explicitly authorized by the operator.
5. Install the matching fixed migration CLI, regenerate a package from authoritative
   production NzbDav, and import it through the normal package workflow.
6. Reconcile, generate a new immutable plan, apply 30 create-only symlinks, and run
   bounded size/read/seek validation.

Cleanup and re-import are separate gates. A cleanup failure stops before reset; an
import or naming failure leaves `/mnt/plex2` unregistered and stops before any Plex
or Arr change.

## Acceptance

- 16 of 16 imported history filenames and job names match legacy NzbDav.
- No imported job directory has an unintended UUID name.
- No imported media leaf has an unintended UUID basename.
- All 30 selected links correlate exactly and point at exact-size canonical IDs.
- Representative direct and archive files pass bounded reads and seeks.
- InfiniDysk remains healthy with no target naming or streaming errors.
- Legacy NzbDav, `/mnt/plex`, Plex, and Arr are unchanged.
