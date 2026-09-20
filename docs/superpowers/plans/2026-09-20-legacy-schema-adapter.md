# Temporary legacy-schema adapter implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development for implementation and independent spec/quality reviews. This is a correction to the approved NzbDav canary design, not a new migration architecture.

**Goal:** Unblock read-only export from the observed legacy schema without adding legacy columns or permanent application dependencies.

**Architecture:** Resolve release ownership from `DavItems.ParentId` ancestry and `HistoryItems.DownloadDirId`, not names. Recover original NZB bytes from a history-ID blob or retained `NzbContents`; preserve strong article identity, exact size, checksummed packages and existing import journals. Unresolvable or ambiguous ownership remains an explicit exclusion.

**Tech stack:** Existing .NET 10 CLI, Npgsql, xUnit, isolated PostgreSQL integration fixtures.

## Approved boundaries

- `DavItems.HistoryItemId`, `FileBlobId`, and `NzbBlobId` are absent in the live database. The old fixture duplicated that mistaken assumption.
- No database migrations, application settings, setup-wizard changes, legacy writes, Plex changes, automatic full-library import, or filename-only correlation.
- Retain temporary tooling until the **full library** is migrated and accepted, not just until the canary passes. Keep migration evidence afterward.
- Missing-history/orphan NZBs are not automatically associated: report them for identity-backed reconciliation rather than fabricating ownership or lossy NZBs.

## Task 1: Correct the reader and prove the real schema

Files: `tools/NzbDavMigration/Legacy/LegacyNzbDavReader.cs`, reader tests under `tests/NzbWebDAV.Tests/UsenetMigration/`.

- [ ] Run focused existing tests as a baseline.
- [ ] Replace the fictitious SQL fixture with actual legacy columns. Observe the old reader fail on absent columns.
- [ ] Add nested ancestry, duplicate-owner, no-history, cyclic-parent, missing-row and SELECT-only/writer-login cases.
- [ ] Implement bounded read-only resolution with explicit exclusions and preserve the proven release-root path.
- [ ] Remove unused `FileBlobId` data plumbing; update affected fixture constructors.
- [ ] Run the focused tests against an isolated local PostgreSQL instance and commit.

## Task 2: Carry retained NZBs safely through export

Files: legacy blob resolver, inventory service, identity extractor, package writer and CLI in `tools/NzbDavMigration/`; colocated migration tests.

- [ ] Add red tests for missing-blob inline fallback, XML/byte limits, identity mismatch and nested archive paths.
- [ ] Preserve exact validated NZB bytes in checksummed output instead of reopening a mutable source after validation.
- [ ] Test the camelCase inventory-to-export round trip and private artifact permissions.
- [ ] Keep missing/ambiguous records excluded; preserve the 20–50 reviewed-selection gate and existing resumability/duplicate safeguards.
- [ ] Run focused migration regressions and commit.

## Task 3: Review, document and hand off

Files: `docs/guides/nzbdav-migration.md`, this plan.

- [ ] Document actual schema resolution, private reports, exclusions and temporary-tool retirement after full acceptance.
- [ ] Perform independent spec and code-quality review; address findings and rerun affected tests.
- [ ] Commit, push `chore/legacy-schema-adapter`, open a PR in `johoja12/infinidysk`, verify its head and leave primary checkout clean on `main`.
- [ ] Report source/test status separately from live canary status. A new PR is not implicitly authorized for merge or deployment.

## Focused verification

```bash
DATABASE_PROVIDER=postgres \
DATABASE_CONNECTION_STRING='<isolated test PostgreSQL connection>' \
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~UsenetMigration'
```

The PostgreSQL container is test-only with tmpfs storage, loopback-only binding and no production volumes. Existing migration tests are focused regression coverage for this compatibility failure; unrelated CI suites are not repeated locally.
