# NzbDav export package v1

This reference describes the offline, read-only package accepted by the NzbDav migration source. It is an implementation contract, not a general backup format.

## Layout

```text
package-root/
├── manifest.json
├── SHA256SUMS
├── success.json
├── exclusions.json
└── payloads/
    └── <source-release-id>.nzb
```

`SHA256SUMS` contains a lowercase SHA-256 digest and package-relative path for `manifest.json`, both reports, and every payload. Paths must use `/`, must be relative, and may not contain empty, `.` or `..` components. The reader rejects missing, extra, duplicate, malformed, path-escaping, size-mismatched, or digest-mismatched payload entries.

## Manifest

`manifest.json` uses schema version `1`, source `nzbdav`, and contains:

- package ID and UTC creation time;
- releases with source release ID, legacy NZB blob ID, payload path, and leaves;
- selected library links with library-relative path, original target, and legacy DavItem ID;
- payload lengths and digests;
- checksum metadata.

Each leaf records its legacy DavItem ID/path, exact byte size, parent release, history/blob provenance, identity kind/digest, extraction status, and any exclusion reason. InfiniDysk accepts only the defined schema and rejects unknown JSON members.

## Trust boundary

Generate the package beside legacy NzbDav with a SELECT-only database account. The package contains NZB payloads and legacy paths/identifiers, but must never contain the database connection string or credentials. Transfer the completed directory and mount it read-only beneath `{CONFIG_PATH}/migration-input`; InfiniDysk refuses source paths outside that boundary.

The package is immutable after export. If any byte or selection changes, generate a new package with a new ID rather than editing files or recalculating checksums manually.

## Reports

`success.json` lists leaves with strong extracted identity. `exclusions.json` lists all other exported leaves and their reasons. These reports are evidence, not an override: package verification and scanner policy remain authoritative.
