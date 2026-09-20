# Native cache and Smart Prefetch implementation report

Testing branch: `feat/smart-prefetch-native-cache`, based on `a809fa2e120ef029dd0875973b12be0147615411`.
[Draft testing PR](https://github.com/johoja12/infinidysk/pull/4).
Recorded on 2026-09-20. No merge or production deployment was performed.

## Delivered scope

| Area | Implementation and evidence |
| --- | --- |
| Exclusive caching | Off/Segment/Native resolver, atomic legacy-alias validation, environment ownership, restart boundaries and setup preservation; no Native-to-Segment fallback |
| Native storage | Multiple folders, sparse final-media files, verified 4 MiB blocks, local indexed SQLite catalogue, durable journals/checkpoints, bounded leases/buffers, revision fencing and whole-generation eviction |
| Native operations | Folder editor, quota/reserve/age/priority/read-only/watermarks, bounded probe/scan/clear, pinning, generation/range browser, aggregate metrics and memory diagnostics |
| Warming | One persistent bounded queue, manual/bulk outcomes, overlap coalescing, ownership pruning, pause/cancel/retry/priority, low-priority shared transport and durable provider-payload budget |
| Plex | Browser PIN sign-in, account/Home user support, server discovery/test/save, multiple servers, exact mappings, libraries/users/hubs/collections, source selections/exclusions and previews |
| Smart policies | Configurable realtime/history/read signals, user-scoped next episodes, prediction limits/confidence/cooldown, minimum/full warming, playback pause and fail-closed warming metadata faults |
| Existing owners retained | Shared streams, adaptive read-ahead, NNTP pool/admission/bandwidth, article-memory budget, repair and discovery stay authoritative; no second downloader or local-file cache |

## Automated and isolated verification

The final regression evidence is recorded below; these are selected suites, not a
claim that every repository or real-provider integration test was run.

- Backend selected regression suite: **596 passed, 0 failed, 0 skipped**. Includes
  native filesystem/journal/pressure/faults, mode and configuration, Plex policies,
  queue/budget, HTTP runtime-failure contracts, content fidelity, shared streams,
  setup, support-pack privacy and diagnostics.
- Deterministic/loopback NNTP suites: **318 passed, 0 failed, 0 skipped**, using the
  built Linux x64 native yEnc library. No real-provider integration tests were run.
- Frontend: **61 passed across 9 files**, plus typecheck, scoped ESLint and
  production frontend/server builds.
- Application, UsenetSharp and benchmark builds with repository analyzers enabled:
  **0 warnings, 0 errors**. Backend test compilation also passed with analyzers enabled.
- Admin OpenAPI export/contract test passed; generated contract is committed.
- Strict documentation build passed.
- Real isolated frontend/backend browser smoke saved two Native folders, restarted
  the test backend, observed active Native mode, completed a write probe and enabled
  policies. Browser console/page errors: none. Test servers were stopped afterward.
- Plex browser sign-in/discovery/test/save was exercised with **mock HTTP responses**;
  this verifies UI wiring, not real Plex authentication or account behavior.

The final pass caught a test-fixture cleanup race: memory snapshot capture correctly
does not await native storage initialization, but the fixture deleted its temporary
directory before late initialization ended. The fixture now explicitly waits for
its owned background work before deleting test data; runtime bounded shutdown is
unchanged. The focused cache-service suite passed all 15 tests after this fix.

## Scale evidence and limits

The committed [50 TB metadata report](https://github.com/johoja12/infinidysk/blob/feat/smart-prefetch-native-cache/backend.Benchmarks/Reports/native-cache-scale-50tb-2026-09-20.json)
models 5,000 files of 10 GB and 11,924,999 block rows, with **zero media payload files**.
It produced a 2.54 GB local catalogue and a 76.2 MB peak process working set during
the measured run. Median warm reopen was 1.40 ms; recorded query plans use indexes.
These measurements are a synthetic local-catalogue check, not a NAS throughput,
power-loss recovery, or 50 TB end-to-end deployment benchmark.

Native is the recommended architecture for this user's HDD/NAS, whole-movie/episode
workload. Extending Segment to multiple folders alone would retain article-file
cardinality and repeated final-format decoding. Actual cold/warm playback and NAS
comparisons still need the same media corpus on the target hardware.

## Outstanding operational acceptance

Keep this PR in draft/testing status until the [canary procedure](native-cache-prefetch.md)
records real authenticated Plex/Home-user/multi-server checks, target HDD/NAS fault
and playback comparisons, and a bounded **24-hour soak**. No claim is made that these
hardware/account-dependent gates have passed. Use separate configuration and cache
roots; back up `/config` before testing an upgrade. Never share writable cache roots
between production and the test instance.

At handoff the fork exposed no GitHub Actions runs or PR check results. Local
verification is recorded here, but CI approval is not claimed; run the normal PR
lanes before considering a merge.

The original design/plan checklists remain planning records. This report describes
what was implemented and distinguishes automated evidence from pending operational
acceptance; it does not retroactively mark unperformed live checks complete.
