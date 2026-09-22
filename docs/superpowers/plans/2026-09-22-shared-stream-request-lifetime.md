# Shared-stream Request Lifetime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent detached native-cache/shared-stream source openers from accessing a disposed WebDAV `HttpContext`, then prove the fix on nuc-1 with fresh cold reads.

**Architecture:** `BaseStoreStreamFile` remains the only request-aware layer. It publishes `DavItem` attribution before wrapping a stream, returns the immutable `DavItem` directly in detached leases, and no longer exposes `HttpContext` to concrete source openers. Direct NZB, RAR, and multipart classes become request-independent media-source factories.

**Tech Stack:** .NET 10, ASP.NET Core `HttpContext`, xUnit, EF Core SQLite, Docker Compose, rclone WebDAV/VFS, InfiniDysk migration CLI.

---

### Task 1: Add request-lifetime regressions

**Files:**
- Create: `tests/NzbWebDAV.Tests/WebDav/DatabaseStoreDetachedLifetimeTests.cs`
- Modify: `tests/NzbWebDAV.Tests/WebDav/BaseStoreStreamFileTests.cs`

- [ ] **Step 1: Add concrete direct and multipart source-opening regressions**

Create an in-memory SQLite fixture with an open `SqliteConnection`, an empty `DavDatabaseContext`, and a `DavDatabaseClient`. Add exposed test subclasses whose public `OpenSourceAsync` calls `base.GetStreamAsync`. For a direct `NzbFile` and a `MultipartFile`, uninitialize the `DefaultHttpContext` before opening and assert the factory reaches the expected missing-payload boundary:

```csharp
[Theory]
[InlineData(DavItem.ItemSubType.NzbFile)]
[InlineData(DavItem.ItemSubType.MultipartFile)]
public async Task DeferredSourceOpen_AfterRequestDisposed_DoesNotAccessHttpContext(
    DavItem.ItemSubType subType)
{
    var context = new DefaultHttpContext(new FeatureCollection());
    var item = new DavItem
    {
        Id = Guid.NewGuid(),
        Name = "movie.mkv",
        Type = DavItem.ItemType.UsenetFile,
        SubType = subType,
        FileSize = 1,
    };
    var file = CreateExposedStoreFile(subType, item, context, _database);

    context.Uninitialize();

    await Assert.ThrowsAsync<MissingFilePayloadException>(
        () => file.OpenSourceAsync(CancellationToken.None));
}
```

The helper must pass `null!` only for NNTP/resolver/budget dependencies that are unreachable because the empty database produces `MissingFilePayloadException` first.

- [ ] **Step 2: Add a detached-lease regression around the await boundary**

Add a `DeferredStoreFile` to `BaseStoreStreamFileTests` whose `GetStreamAsync` signals `Entered` and waits on a `TaskCompletionSource<Stream>`. Start `GetDetachedReadableStreamAsync`, wait until the source opener is entered, uninitialize the request context, then release the source stream and assert the lease still carries the immutable `DavItem`:

```csharp
[Fact]
public async Task DetachedLease_DoesNotReadRequestItemsAfterSourceOpen()
{
    var (context, _) = NewContext();
    var item = new DavItem { Id = Guid.NewGuid(), Name = "movie.mkv" };
    var file = new DeferredStoreFile(context, new ConfigManager(), item);

    var pending = file.GetDetachedReadableStreamAsync(CancellationToken.None);
    await file.Entered.Task;
    context.Uninitialize();
    file.Release.SetResult(TestStreams.Create([1]));

    var lease = await pending;
    Assert.Same(item, lease.DavItem);
    await lease.Ownership.DisposeAsync();
    await lease.Stream.DisposeAsync();
}
```

- [ ] **Step 3: Run the new tests and verify RED**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter "FullyQualifiedName~DatabaseStoreDetachedLifetimeTests|FullyQualifiedName~DetachedLease_DoesNotReadRequestItemsAfterSourceOpen" \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: the direct and multipart cases fail with `ObjectDisposedException: IFeatureCollection has been disposed`; the detached-lease case fails at the post-open `Context.Items` read.

- [ ] **Step 4: Commit the failing regressions**

```bash
git add tests/NzbWebDAV.Tests/WebDav/DatabaseStoreDetachedLifetimeTests.cs \
  tests/NzbWebDAV.Tests/WebDav/BaseStoreStreamFileTests.cs
git commit -m "chore(test): reproduce detached request lifetime failure"
```

### Task 2: Remove request coupling from deferred source openers

**Files:**
- Modify: `backend/WebDav/Base/BaseStoreStreamFile.cs`
- Modify: `backend/WebDav/DatabaseStoreNzbFile.cs`
- Modify: `backend/WebDav/DatabaseStoreRarFile.cs`
- Modify: `backend/WebDav/DatabaseStoreMultipartFile.cs`

- [ ] **Step 1: Make request ownership private to the base class**

Remove the protected `Context` property. Use the primary-constructor `context` field inside `BaseStoreStreamFile`. Return `DavItem` directly from `GetDetachedReadableStreamAsync` after `OpenFinalStreamAsync`:

```csharp
return new DetachedStreamLease
{
    Stream = stream,
    Ownership = ownership,
    DavItem = DavItem,
    ContentIdentity = ContentIdentity,
};
```

Keep the synchronous pre-open attribution in `OpenFinalStreamAsync`:

```csharp
context.Items["DavItem"] = item;
var native = context.RequestServices?.GetService<NativeCacheService>();
```

Use `context.RequestServices` when creating the streaming scope.

- [ ] **Step 2: Remove concrete request writes**

Reduce each concrete `GetStreamAsync` to its media-source factory call. For example:

```csharp
protected override Task<Stream> GetStreamAsync(CancellationToken cancellationToken) =>
    DavContentStreamFactory.OpenNzbAsync(davNzbFile, dbClient, usenetClient, Config,
        inFlightArticleBudget, cancellationToken);
```

Apply the same change to RAR and multipart. Do not catch `ObjectDisposedException`.

- [ ] **Step 3: Run the regressions and verify GREEN**

Run the Task 1 command. Expected: 3 tests pass, with the theory contributing two cases.

- [ ] **Step 4: Run the focused lifecycle suite**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter "FullyQualifiedName~DatabaseStoreDetachedLifetimeTests|FullyQualifiedName~BaseStoreStreamFileTests|FullyQualifiedName~SharedStreamHandlerTests|FullyQualifiedName~SharedStreamRegistryTests|FullyQualifiedName~NativeCachedStream" \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: zero failures.

- [ ] **Step 5: Commit the production fix**

```bash
git add backend/WebDav/Base/BaseStoreStreamFile.cs \
  backend/WebDav/DatabaseStoreNzbFile.cs \
  backend/WebDav/DatabaseStoreRarFile.cs \
  backend/WebDav/DatabaseStoreMultipartFile.cs
git commit -m "fix(webdav): detach stream opening from request lifetime"
```

### Task 3: Verify, push, and merge

**Files:**
- Verify all files changed by Tasks 1-2 and the design/plan documents.

- [ ] **Step 1: Run final local verification**

```bash
git diff --check main...HEAD
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter "FullyQualifiedName~DatabaseStoreDetachedLifetimeTests|FullyQualifiedName~BaseStoreStreamFileTests|FullyQualifiedName~SharedStreamHandlerTests|FullyQualifiedName~SharedStreamRegistryTests|FullyQualifiedName~NativeCachedStream" \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: clean diff and zero failed tests. CI covers the broader backend, library, formatting, PostgreSQL, HTTP-contract, frontend, documentation, mutation, and CodeQL lanes.

- [ ] **Step 2: Push and open the PR**

```bash
git push -u origin fix/shared-stream-request-lifetime
gh pr create --repo johoja12/infinidysk --base main \
  --head fix/shared-stream-request-lifetime \
  --title "fix(webdav): keep playback streams alive after clients change ranges" \
  --body-file -
```

The PR body must link issue #23, summarize the request-lifetime fix, include the RED/GREEN evidence and final focused count, and state that setup-wizard impact is none.

- [ ] **Step 3: Wait for required checks and verify the exact head**

Verify the PR head equals local `HEAD`, all substantive lanes and `Required quality gate` succeed, the PR is mergeable, and no unexpected review block exists. Optional coverage-upload failures do not override a successful required quality gate.

- [ ] **Step 4: Merge the exact PR**

The user's `do 1-5` instruction authorizes this exact merge:

```bash
pr_number=$(gh pr view --repo johoja12/infinidysk --json number --jq .number)
gh pr merge "$pr_number" --repo johoja12/infinidysk --merge
git -C /opt/projects/infinidysk pull --ff-only
```

Verify the PR state is `MERGED`, record the merge commit, and prove local `main` equals `origin/main`.

### Task 4: Deploy the exact merge to nuc-1

**Files and state:**
- Read: `DEPLOYMENT-NOTES.md`
- Preserve: `/opt/docker/infinidysk/docker-compose.yml`
- Modify: `/opt/docker/infinidysk/docker-compose.override.yml`
- Create private backup/evidence under `/opt/infinidysk-migration/backups/` and `/opt/infinidysk-migration/full/`

- [ ] **Step 1: Build and transfer the exact image**

From merged `main`, set `merge_sha=$(git rev-parse HEAD)` and `image_tag=${merge_sha:0:8}`. Build with revision metadata and verify the image label:

```bash
docker build --build-arg VCS_REF="$merge_sha" -t "local/infinidysk:$image_tag" .
docker image inspect "local/infinidysk:$image_tag" \
  --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
docker save "local/infinidysk:$image_tag" | gzip -1 \
  | ssh ubuntu@192.168.20.65 'gzip -d | docker load'
```

Expected: local and nuc-1 image labels equal the full merge SHA.

- [ ] **Step 2: Capture rollback state and back up `/config` plus PostgreSQL**

Require queue depth zero, verify the existing app image/revision and both FUSE mounts, record container start times/restart counts, and create a root-only backup containing:

- Compose plus protected environment files
- `/opt/infinidysk/config`
- a custom-format `pg_dump`
- service and mount snapshots
- SHA-256 checksums

Stop only `infinidysk` for the consistent application backup. Leave PostgreSQL, legacy NzbDav, both rclone containers, Plex, and Arr running.

- [ ] **Step 3: Pin and recreate only the InfiniDysk application**

Write the override with the new image while preserving both existing mounts:

```yaml
services:
  infinidysk:
    image: local/infinidysk:$image_tag
    pull_policy: never
    volumes:
      - /opt/infinidysk-migration/input:/config/migration-input:ro
      - /mnt/nzbdav-cache3/infinidysk:/cache/debrid-cache
```

Validate Compose with `--env-file secrets/app.env`, then run:

```bash
docker compose --env-file secrets/app.env up -d --no-deps --pull never infinidysk
```

- [ ] **Step 4: Verify runtime acceptance**

Wait for healthy and verify:

- running image and revision equal the merge commit
- restart count zero
- frontend `3004/healthz`, backend `192.168.20.65:8080/health`, and public `/healthz` return 200
- unauthenticated WebDAV returns 401
- PostgreSQL remains healthy
- `/mnt/remote/infinidysk` and `/mnt/remote/nzbdav` remain readable FUSE mounts
- `/mnt/plex2` remains 30 links, `/mnt/plex` remains at its fresh baseline, and no container mounts `/mnt/plex2`
- legacy container start times/restart counts are unchanged

### Task 5: Run a fresh six-file cold canary

**Artifacts:**
- Create a root-only evidence directory under the retained Phase B run.
- Create: selection, cache/native status before/after, direct/mount jobs, JSON/Markdown results, filtered logs, state snapshots, and `SHA256SUMS`.

- [ ] **Step 1: Select six genuinely unread files**

From the remaining 30-link `/mnt/plex2` canary, exclude every file used by the original and additional-six reports. Require three direct and three RAR/multipart files. For each InfiniDysk target, require no rclone `vfsMeta` cached ranges and no native-cache verified entry; do not clear either cache to manufacture cold state.

- [ ] **Step 2: Capture before state**

Capture authenticated `/api/native-cache`, rclone RC stats on ports 5573/5574, exact cache-range evidence, application/rclone logs cursor, image/revision/health, container start times, mounts, and `/mnt/plex{,2}` link counts.

- [ ] **Step 3: Run the branch CLI six-file benchmark**

Publish the merged migration CLI self-contained for `linux-x64`, install it in a root-only versioned tool directory on nuc-1, and point `tools/current` to the exact merge tool. Run first/repeat sequential plus 10/50/90 seek probes with a 180-second bound per operation against direct backend ports `8081` and `8080`.

Expected: 96 observations, zero timeouts, zero errors, zero byte mismatches, and exact `vfsMeta` cache bytes/percent in every classified row.

- [ ] **Step 4: Compare direct backend and rclone mount routes**

Choose two non-overlapping uncached 32 MiB windows per file/side, alternate route order, and run first/repeat passes through container-local `rclone cat` and host mount reads. Expected: 48 observations with zero timeouts/errors/mismatches.

- [ ] **Step 5: Capture after state and apply the acceptance gate**

Capture native-cache/rclone counters and bounded logs again. Pass only when:

- all 144 observations are byte-correct and complete
- no `IFeatureCollection has been disposed` or shared-pump disposal failure appears
- native-cache `ioTimeouts` does not increase
- any fallback delta is directly explained by retained logs
- production remains healthy with no unexpected restart or mount drift

If the gate fails, retain evidence and stop before Plex. Do not clear caches, rewrite `/mnt/plex`, register `/mnt/plex2`, or restart adjacent services.

- [ ] **Step 6: Seal evidence**

Generate a recursive `SHA256SUMS` excluding the checksum file and its temporary file, verify every entry, set artifacts to mode `0600`, and record the evidence path in issue #23 and the final report.
