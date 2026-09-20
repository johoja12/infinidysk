# Native cache and Smart Prefetch testing

[since unreleased testing branch](https://github.com/johoja12/infinidysk/pull/4){ .nzbdav-since }

## Isolation

Use the testing branch's image, separate ports, a **new** configuration directory,
and **new** cache directories. Never mount a production catalogue/cache writable in
two instances. Do not point production Sonarr/Radarr at the test instance or enable
automatic upgrades. Back up `/config` before testing an existing configuration copy.

Build an explicitly tagged local image from the testing worktree:

```bash
docker build -t infinidysk:native-cache-test .
```

Run using your normal container configuration with different host ports and dedicated
bind mounts. Keep metadata on local storage. Map two test folders (for example,
`/cache-a` and `/cache-b`) into the container; configure those container paths in the
UI. Start with small quotas and no automatic Plex sources. No test procedure here
requires restarting, replacing, or clearing production containers.

## Deterministic regression checks

CI owns normal build/test lanes. For focused diagnosis on a Linux x64 development host:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj \
  -p:RapidYencRuntimeIdentifier=linux-x64 \
  --filter 'FullyQualifiedName~Native|FullyQualifiedName~Prefetch|FullyQualifiedName~Plex|FullyQualifiedName~CacheMode|FullyQualifiedName~SharedStream'
cd frontend
NODE_ENV=test npx vitest run app/routes/settings/plex app/routes/settings/smart-prefetch \
  app/routes/settings/streaming/native-cache.component.test.tsx \
  app/routes/settings/streaming/native-cache-model.test.ts app/routes/setup
npm run typecheck
```

Use the appropriate native runtime identifier on other supported hosts. Do not run
real-provider UsenetSharp integration tests without separately configuring and
authorizing their network access. Loopback transport tests need no provider secrets;
set `RAPIDYENC_LIBRARY_PATH` to the built native library or its directory on
`LD_LIBRARY_PATH` when running a test host that does not copy the library beside it.

## Metadata-only scale report

The scale report creates and removes its own unique temporary local catalogue. It
does not create 50 TB of media or contact Plex/Usenet/NAS. Start with `smoke`; the
`50tb` fixture represents 5,000 files of 10 GB with roughly 12 million verified block
rows. Allow local disk space for SQLite/indexes. Run timing work outside CI:

```bash
dotnet run --project backend.Benchmarks -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 -- \
  --native-cache-scale-report --shape smoke --samples 10
dotnet run --project backend.Benchmarks -c Release \
  -p:RapidYencRuntimeIdentifier=linux-x64 -- \
  --native-cache-scale-report --shape 50tb --samples 10
```

Record startup/query timings, query plans, catalogue size, managed allocation and
working set. These are warm local-catalogue measurements, not cold NAS latency,
media throughput, power-loss recovery, or proof that a 50 TB production cache is safe.

## Opt-in HDD/NAS and Plex canary

Use only disposable cache data for failure injection. Never disconnect a shared
production mount or deliberately fill a production filesystem.

1. Verify Off, Segment and Native separately. Native must never read/write Segment
   files. Switching modes must retain inactive cache data unless explicitly cleared.
2. Import one disposable movie and episode. Compare source and warm-cache bytes,
   range/seek behavior and latency; cover RAR, multipart and encrypted media when used.
3. Add two native folders. Check placement priority, quota/reserve, high/low eviction,
   read-only hits, pinning, bounded browser/range pages, probe and explicit scan.
4. Warm a full movie and partial ranges; merge overlapping requests from multiple
   sources. Pause, cancel, retry and restart. Confirm only manual work restores paused
   and already verified bytes do not consume provider payload budget again.
5. Test an offline/replaced **test** volume, permission failure and interrupted writes.
   Confirm fallback remains available, incomplete ranges are never hits, late IO
   retains bounded memory admission, and reconciliation does not resurrect deleted data.
6. Complete real browser Plex login, expiration/cancel/retry, Home PIN switching,
   discovery, connection test and masked save. Verify two servers and two users with
   different watch histories; never infer one user's unwatched state from another token.
7. Fetch hubs/collections, configure exact mappings/exclusions, inspect source and
   policy previews, and verify realtime/history/next-episode/minimum/full policies.
   Disconnect an account, disable a source, and edit mappings during a refresh.
8. Run at least a 24-hour bounded soak with playback plus warming. Record foreground
   stalls, source fallback counts, NNTP payload budget, memory plateau, cache growth,
   eviction progress, policy precision and recovery. Compare against Off/Segment using
   the same files and request pattern; do not hide cold-cache setup costs.

Keep the PR in testing status until the relevant hardware and authenticated Plex
checks are recorded. Automated tests alone do not satisfy this operational gate.
