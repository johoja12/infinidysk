# Cache and Plex Prefetch Execution Contracts

Implementation status: see the [testing-branch report](../../testing/native-cache-implementation-report.md),
including the distinction between automated verification and outstanding live gates.

> **For agentic workers:** Use `superpowers:executing-plans` for sequential
> execution. This document refines the linked implementation plan; unchecked
> items are future work, not implemented features.

**Goal:** Make the accepted planning direction executable as small, reviewable
changes with explicit configuration, persistence, API and release gates.

**Architecture:** Off/Segment/Native are exclusive modes. Native stores final
media bytes across configured folders with local indexed metadata; Plex producers
feed one bounded prefetch queue through existing streaming/NNTP admission.

**Tech Stack:** Existing .NET 10/xUnit, SQLite infrastructure, React Router/Vitest,
admin OpenAPI generation, Docker testing workflow and repository CI.

## Planning status and document precedence

The user requested detailed planning after the 50 TB comparison on 2026-09-20.
Accepted planning direction: HDD/NAS, mostly whole media; native final-file cache,
mutually exclusive Segment alternative, multiple native folders, configurable
Plex feature parity. No implementation, deployment or PR merge is implied.

Read with the [implementation task list](2026-09-20-smart-prefetch-native-cache.md),
[design](../specs/2026-09-20-smart-prefetch-native-cache-design.md), and
[capacity comparison](../specs/2026-09-20-50tb-cache-architecture-comparison.md).
This document resolves execution details and ordering. Hardware-dependent tuning
remains subject to measurement; required user-facing capabilities do not.

## Dependency order and deliverables

| Slice | Existing tasks | Required predecessor | Artifact and exit condition |
| --- | --- | --- | --- |
| 0. Baseline | 0 | None | Pinned source/corpus/config and recorded existing-mode behavior |
| 1. Cache-mode contract | Mode portion of 4 and 7 | 0 | One resolver/validator; no coexistence across startup, saves, wizard or client rebuild |
| 2. Content/fidelity foundation | 1–2 | 1 | Common stream ownership; exact evidence and durable revision fence |
| 3. Storage engine | 3 and 5 | 2 | Multi-folder byte store, bounded local catalogue, crash/lease tests |
| 4. Usable native cache | Rest of 4 and folder portion of 7 | 3 | Read-through, complete folder editor, exact native hits and mode-switch canary |
| 5. Manual warming | 6 and remaining operation UI in 7 | 4 | Bounded queue, native-only admission, pause/cancel/resume and committed progress |
| 6. Plex connection/catalogue | Account/catalogue portions of 8 | 5 | PIN login, multiple servers, libraries/users, hubs/collections and saved previews |
| 7. Complete smart policies | Rest of 8–9 | 6 | All required realtime/history/source controls, predictions and operations |
| 8. Scale and playback gates | 10 throughout, final acceptance here | 7 | CI evidence, actual NAS comparisons and isolated playback/soak report |

Keep one integration branch `feat/smart-prefetch-native-cache` with a draft PR to
the fork; use small commits within it. Do not merge partial slices to main or
introduce temporary fallback behavior to claim a milestone passed. Slice 1 can
validate native selection without activating unfinished native services; test
the factory with fakes and keep the runtime option unavailable until Slice 4.

## Slice 1: exact cache-mode behavior

**Create:** `backend/Config/CacheModeResolver.cs`,
`tests/NzbWebDAV.Tests/Config/CacheModeResolverTests.cs`.
**Modify:** `backend/Services/ConfigUpdateService.cs`,
`backend/Config/ConfigManager.cs`, `ConfigKeys.cs`,
`backend/Clients/Usenet/UsenetStreamingClient.cs`,
`backend/Services/SegmentCacheCleanupService.cs`,
`backend/Services/SetupWizardService.cs`,
`frontend/app/routes/settings/streaming/streaming.tsx`,
`frontend/app/routes/settings/validation.ts` and setup model/steps.

Source findings: `UpdateConfigController` delegates to `ConfigUpdateService`;
wizard completion calls its `StageAsync`/`Publish` methods. Therefore validation
belongs in shared staging, not only the controller or React. The wizard currently
forces Segment on for STRM and off for symlinks and computes restart from that
boolean. Change all three behaviors together.

| Inputs | Effective result |
| --- | --- |
| No explicit mode, legacy absent/false | Off |
| No explicit mode, legacy true | Segment |
| Explicit mode with old persisted legacy value | Mode is authoritative; atomically canonicalize persisted alias on save |
| Explicit Native + environment legacy true | Reject with both environment key names; no partial write |
| Explicit Segment + environment legacy false | Reject with both environment key names; no partial write |
| Explicit Native/Off + environment legacy false | Valid; legacy false means segment disabled |
| Mode save conflicting with environment-owned mode | Reject; retain old configuration and active mode |
| Contradictory mode and alias in one API request | Reject the entire request |
| Legacy-only API update after explicit mode exists | Translate true to Segment/false to Off through the same validator; never leave Native active |

The last row deliberately makes a legacy API toggle a mode change, requiring
restart. Return the resolved pending mode to the caller; a legacy client cannot
quietly change one half of an exclusive configuration.

Pure resolution contract to implement, with syntax/type tests in the first commit:

```csharp
namespace NzbWebDAV.Config;

public enum CacheMode { Off, Segment, Native }

public static class CacheModeResolver
{
    public static CacheMode Resolve(
        string? explicitMode, bool? legacySegment, bool legacyEnvironmentOwned)
    {
        CacheMode mode;
        if (string.IsNullOrWhiteSpace(explicitMode))
            mode = legacySegment == true ? CacheMode.Segment : CacheMode.Off;
        else
            mode = explicitMode.Trim().ToLowerInvariant() switch
            {
                "off" => CacheMode.Off,
                "segment" => CacheMode.Segment,
                "native" => CacheMode.Native,
                _ => throw new ArgumentException("cache.mode must be off, segment or native.")
            };

        if (legacyEnvironmentOwned && legacySegment.HasValue &&
            legacySegment.Value != (mode == CacheMode.Segment))
            throw new ArgumentException(
                "cache.mode conflicts with environment-owned usenet.segment-cache.enabled.");
        return mode;
    }
}
```

This pure resolver is not the complete save validator: staging must additionally
check explicit values submitted together, mode environment ownership, native
folder readiness and alias canonicalization in one transaction.

```csharp
using NzbWebDAV.Config;
using Xunit;

public class CacheModeResolverTests
{
    [Theory]
    [InlineData(null, null, CacheMode.Off)]
    [InlineData(null, true, CacheMode.Segment)]
    [InlineData("native", true, CacheMode.Native)]
    [InlineData("off", true, CacheMode.Off)]
    public void PersistedLegacyCannotOverrideExplicitMode(
        string? configured, bool? legacy, CacheMode expected) =>
        Assert.Equal(expected, CacheModeResolver.Resolve(configured, legacy, false));

    [Theory]
    [InlineData("native", true)]
    [InlineData("segment", false)]
    public void ConflictingEnvironmentAliasIsRejected(string mode, bool legacy) =>
        Assert.Throws<ArgumentException>(() => CacheModeResolver.Resolve(mode, legacy, true));

    [Fact]
    public void InvalidModeIsRejected() =>
        Assert.Throws<ArgumentException>(() => CacheModeResolver.Resolve("both", null, false));
}
```

- [ ] Add the resolver tests before the implementation. For focused development
  diagnosis, run `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter FullyQualifiedName~CacheModeResolverTests`.
  Expected initial failure: missing resolver type; after implementation: all cases pass.
- [ ] Add staging tests to `Api/EnvironmentManagedConfigApiTests.cs`: invalid batch
  writes nothing, concurrent saves do not publish mixed settings, and environment
  values remain authoritative. Include client-chain creation on provider reconfigure.
- [ ] Implement staging atomically, active/pending mode reporting and restart
  requirements. Keep the active mode immutable for a process lifetime.
- [ ] On new setup, retain the existing suggested STRM/Segment and symlinks/Off
  choices. On rerun with explicit mode, preserve that choice and show Review;
  do not force the legacy boolean or replace Native. Apply the shared validator.
  Test environment-managed Native, folder absence and changed mode in Review.
- [ ] Test cleanup retention for explicit Native/Off and unchanged legacy cleanup.
- [ ] Commit `feat(cache): select one persistent cache mode at a time`.

## Slice 2–4: persistence and streaming contracts

Every read request pins a content generation. A repair/remap fences further old
publications and drains affected shared streams. Do not combine old and new
generation bytes in one response; cancel/fail that stream through existing error
handling when its pinned generation is no longer safe, allowing a clean retry.

Logical ranges are half-open and checked for overflow. Persist file size and
revision on the generation. The range map, not sparse file length, authorizes hits.
Native hit verification should read only bounded integrity units; record any
read amplification and tune units on actual small seeks, not just sequential I/O.

Auxiliary catalogue responsibilities (logical schema; keep payloads out of SQLite):

| Entity | Key and fields | Index/query obligation |
| --- | --- | --- |
| FolderSnapshot | Folder ID, config generation, filesystem identity, quota/allocated/reserved totals, health | Shared-filesystem free-space reservations |
| ContentGeneration | Item ID + revision, owner folder, size, committed bytes, allocated bytes, retention, access bucket, lifecycle state | `(folder, retention, state, access_bucket, item)` bounded eviction |
| VerifiedRange | Generation + start, end, integrity hash, data offset, journal sequence | Per-generation range lookup; never load all generations |
| DirtyGeneration | Generation, mutation epoch, checkpoint sequence | Dirty-only recovery and idempotent replay |
| ManualWarmJob | Job ID, generation, requested range, status, cancel/resume revision | Paused restoration and paginated operator queue |

No SQL row per playback buffer. Batch access updates; maintain one bounded writer
queue. Preserve local database schema version and explicit rebuild tooling.
Catalogue corruption is a cache miss/rebuild condition, not lost media identity.

Publication state machine:

```text
Acquire activity lease and byte reservation
  -> persist dirty-generation marker
  -> recheck source generation and verified range evidence
  -> write unpublished gap and flush data
  -> append/flush checksummed commit record
  -> expose coverage and update derived catalogue
  -> periodically checkpoint manifest, then retire covered journal records
  -> clear dirty marker only after checkpoint and catalogue agree
```

Inject failure/cancellation after **each** arrow. Recovery may discard valid but
unpublished bytes; it cannot serve uncommitted or stale bytes. A malformed journal
tail must not discard earlier valid commits. Eviction and invalidation require
generation fencing and exclusive ownership, including manual operations.

- [ ] Split Task 3 into separate commits: folder model/placement; durable revision
  identity; data/range journal; catalogue/checkpoint; recovery and fault tests.
- [ ] Add `NativeCacheCatalogStoreTests.cs` alongside the existing planned native
  tests, covering query plans, restart, missing catalogue and bounded pagination.
- [ ] Add per-folder priorities with deterministic ID ties, quotas/free-space reserve,
  age, enabled/read-only and Local/NFS/SMB/Unknown type. Persist bytes as 64-bit
  integers; validate conversions from UI units without overflow.
- [ ] Guard against two directories on the same filesystem reserving the same free
  bytes. Folder removal defaults to keeping data; explicit clear waits for leases.
- [ ] Add `NativeCacheModeTransitionTests.cs`: Native never opens segment files,
  Segment never opens native payloads, and settings/client rebuild cannot start both.
- [ ] Integrate stream factory with lazy native fallback after decryption and below
  shared readers. Preserve HEAD/416 and both frontend-backed DAV and `/view` paths.
- [ ] Finish the folder editor before calling Slice 4 usable; test add/edit/remove,
  probe/scan/clear, read-only results, pending restart, availability and pinned-full.

## Slice 5: one queue, no second transport scheduler

Initial bounds: one worker, 256 intents, 64 MiB windows, 32 MiB physically rented
native-writer buffers. Make concurrency/caps configurable, but clamp them below
existing admission budgets; raising a UI number cannot create provider connections.
Default warming is manual; automatic policies require explicit activation.

Intent lifetime: queued -> running -> completed/deferred/failed/cancelled/expired.
Deferred work retains its attempts/expiry; source refresh cannot reset retry limits
by creating equivalent intents. Merge overlapping intents by content revision and
range while retaining a bounded set of source owners. Disabling one source removes
its ownership; cancel only if no other active source or explicit manual owner remains.

- [ ] Create queue/policy/executor independently with fake clock and transport.
- [ ] Test capacity overflow, pressure pause, user cancellation, source disabling,
  foreground handoff, seek races, same-item duplicates and shutdown.
- [ ] Require Native mode plus a healthy writable root; completed native hits do
  not consume the speculative provider-byte budget. Failed provider attempts do.
- [ ] Manual job persistence restores paused and rechecks generation/coverage.
  Add idempotency keys so retries of a UI request cannot duplicate full-file jobs.
- [ ] Exercise pause/cancel/retry from the UI before introducing Plex producers.

## Slice 6–7: Plex configuration contract

Existing `ConfigSecretMasker` has a finite registry of scalar and JSON secret
properties. Plex credentials must be registered and tested there; naming a field
`Token` does not automatically mask it. Use typed JSON settings with stable IDs
and the existing secret resolver, not custom browser token storage.

Proposed settings groups (typed fields, bounded validation and managed UI):

| Group | Fields exposed to the operator |
| --- | --- |
| `plex.accounts` | Stable account ID, display label, masked Token, connected status; login/reconnect/disconnect |
| `plex.servers` | Machine ID, account reference, selected connection URL, masked Token where distinct, enabled, path mappings |
| `prefetch.plex.sources` | Server/library ID, hub/collection kind and key, media kind, enabled, item limit, excluded TV show IDs |
| `prefetch.plex.users` | Selected history user IDs scoped by server/account |
| `prefetch.realtime` | Enabled, poll interval, verified-session expiry |
| `prefetch.history` | Enabled, sync interval, lookback, minimum episodes, confidence and cooldown |
| `prefetch.triggers` | Plex playback, read activity, predictions, minimum warm, hubs/collections |
| `prefetch.media` | Movies/TV enabled, episodes ahead, episodes per show, resume/minimum range, full-file choice, local-file eligibility |
| `prefetch.limits` | Workers, queue length, per-file/connection caps, speculative bytes/day, retry/expiry limits |

Detailed donor defaults are comparison inputs, not requirements to copy unsafe
values. Populate help text with units and enforced ranges; reject invalid JSON
shape or unknown enum members and apply a complete settings batch atomically.
Keep proposed existing `prefetch.enabled`, `prefetch.mode` and
`prefetch.daily-budget-gb` as their sole owners: groups above are logical UI/model
groups, not duplicate keys for those same values.

Plex transport fetches belong to one typed client, with cancellation, bounded
response size, bounded pagination, source snapshots and coalesced refresh. Persist
selections by machine/library/source IDs; server names and hub titles can change.
Automatic source discovery never auto-enables every returned hub.

- [ ] Implement/test PIN creation/poll/expiry/cancel; bind opaque login ID to the
  initiating admin. Only the backend handles the issued token. Preserve one stable
  installation client ID; disconnect stops associated polling and queued intents.
- [ ] Add discovery/connection tests and multiple saved servers with per-server
  credentials. Source previews cannot forward tokens across origins or redirects.
- [ ] Add library/user selection and movie/TV hub/collection fetch, preview, save,
  refresh, limits and exclusions. Partial server failure retains last-good data
  labeled stale; stale data cannot refresh a verified playback signal.
- [ ] Add history and realtime policies independently; selected Media/Part tests
  must precede next-episode automation. Test multiple editions and ambiguous mapping.
- [ ] Wire trigger controls through the queue ownership model, not separate queues.
- [ ] Test all saved settings survive reload/restart and respect environment-owned
  values; refreshed source catalogues must not reset operator selections.
- [ ] Complete authenticated frontend flow with a mock Plex server, then an opt-in
  real Plex canary on the test instance. No library mutation/scans are necessary
  for login, source discovery or warming existing imported media.

## API outcomes and operator semantics

Use the target action-controller pattern and existing auth, error envelope,
server loaders/actions and OpenAPI generation. Proposed outcomes below must be
represented explicitly in DTOs; do not hide cache errors behind empty successful lists.

| Operation | Outcome requirements |
| --- | --- |
| Save mode/folders/settings | Resolved pending mode, active mode, changed fields, restartRequired; invalid batch changes nothing |
| Folder probe | Availability, writable/read-only, filesystem capability, reason; no unrelated files read/deleted |
| Native entry listing | Cursor/page, owner folder, committed/allocated bytes, generation, retention, exact coverage availability |
| Clear/evict | Retired/completed/busy state; retry token/job identity for bounded async work; no unlink of active generation |
| Queue warm | Job ID, normalized range, owner/source, accepted/deduplicated/rejected reason; actual committed progress |
| Plex login status | Pending/succeeded/expired/cancelled/failed, opaque account handle; no plaintext token |
| Source catalogue/preview | Stable IDs, snapshot time, stale/error state, item limits, mapped/unmapped status and exclusion reasons |
| Status/history | Paginated counts by queued/running/deferred/etc.; consistent metric definitions; no unbounded history |

HTTP validation errors use the existing 400 convention; state conflicts such as
busy eviction use 409 with actionable reason. Asynchronous accepted work uses
202 plus job status. Never count received but uncommitted bytes as cached progress.

## Release gates and first implementation action

- [ ] Gate A: existing-mode baseline and exclusive-mode lifecycle tests pass.
- [ ] Gate B: exact bytes, generation fencing, synthetic-byte exclusion, crash
  publication and leases pass before any background/native production claims.
- [ ] Gate C: multi-folder UI/manual warm works through authenticated frontend;
  all global budgets stay authoritative and no Segment access occurs in Native.
- [ ] Gate D: complete Plex parity table is traced to UI/API/tests, including login,
  discovery, hubs/collections, preview, saved controls and source disable behavior.
- [ ] Gate E: same-host latency/throughput limits from the design pass; synthetic
  catalogue scale, real NAS I/O and occupied-capacity evidence are reported apart.
- [ ] Gate F: isolated test image digest, source SHA, CI results, mode/folder/Plex
  config and rollback are recorded; main/production remain untouched.

Implementation starts with baseline capture and Slice 1's resolver/staging tests,
then proceeds through this dependency table. The full repository test matrix runs
in CI; use focused local runs to diagnose new failures and manual NAS tests for
properties CI cannot prove. Do not mark the entire plan complete at the manual
warming milestone. No runtime code was written as part of preparing this plan.
