# Plex Scoped Source Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore Plex library hubs and collections in Smart Prefetch while retaining either source type when the other Plex request fails.

**Architecture:** Split Plex collection and hub transport calls into independent `PlexApiClient` methods. `PlexCatalogueService` will cache those resources separately and merge their snapshots without changing the controller or frontend contract.

**Tech Stack:** .NET 10, ASP.NET Core, xUnit, `HttpClient`, LINQ to XML

---

## File Structure

- Modify `backend/Services/Plex/PlexApiClient.cs`: expose independent collection and hub reads and use Plex's supported scoped hub route.
- Modify `backend/Services/Plex/PlexCatalogueService.cs`: cache collection and hub snapshots independently and aggregate partial results.
- Modify `tests/NzbWebDAV.Tests/Plex/PlexApiClientTests.cs`: assert exact scoped/global Plex paths and source projection.
- Modify `tests/NzbWebDAV.Tests/Plex/PlexCatalogueServiceTests.cs`: prove partial data survives either upstream failure.

### Task 1: Correct the Scoped Plex Hub Contract

**Files:**
- Modify: `tests/NzbWebDAV.Tests/Plex/PlexApiClientTests.cs:201-220`
- Modify: `backend/Services/Plex/PlexApiClient.cs:126-143`

- [ ] **Step 1: Change the contract test to require Plex's supported scoped path**

In `LibrariesUsersSourcesAndEpisodes_UseTypedBoundedXmlContracts`, replace the scoped hub response arm and add exact request assertions:

```csharp
"/hubs/sections/2" => """<MediaContainer><Hub hubIdentifier="recent" key="/hubs/recent" title="Recent" type="show"/></MediaContainer>""",
```

After the source assertions, add:

```csharp
Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/hubs/sections/2");
Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/library/sections/2/hubs");
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~PlexApiClientTests.LibrariesUsersSourcesAndEpisodes_UseTypedBoundedXmlContracts' \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: FAIL because the current client requests `/library/sections/2/hubs`, so no hub is projected and the exact path assertion fails.

- [ ] **Step 3: Make the minimal endpoint correction**

Change the scoped path in `PlexApiClient.GetSourcesAsync`:

```csharp
var path = string.IsNullOrEmpty(libraryId) ? "/hubs" : $"/hubs/sections/{Identifier(libraryId)}";
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the same command from Step 2.

Expected: PASS, with the fake handler recording `/hubs/sections/2` and never recording the unsupported route.

- [ ] **Step 5: Commit the endpoint correction**

```bash
git add backend/Services/Plex/PlexApiClient.cs tests/NzbWebDAV.Tests/Plex/PlexApiClientTests.cs
git commit -m "fix(plex): use supported scoped hubs endpoint"
```

### Task 2: Preserve Partial Scoped Source Results

**Files:**
- Modify: `tests/NzbWebDAV.Tests/Plex/PlexCatalogueServiceTests.cs`
- Modify: `backend/Services/Plex/PlexApiClient.cs:126-143`
- Modify: `backend/Services/Plex/PlexCatalogueService.cs:19-25`

- [ ] **Step 1: Add failing partial-result tests**

Add these tests to `PlexCatalogueServiceTests`:

```csharp
[Fact]
public async Task ScopedSources_RetainCollectionsWhenHubsFail()
{
    using var handler = new FakePlexHandler(request => request.RequestUri!.AbsolutePath switch
    {
        "/library/sections/2/collections" => PlexApiClientTests.Xml(
            """<MediaContainer><Directory ratingKey="7" key="/library/collections/7/children" title="Shows"/></MediaContainer>"""),
        "/hubs/sections/2" => new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("secret upstream body") },
        _ => PlexApiClientTests.Xml("<MediaContainer/>")
    });
    var catalogue = new PlexCatalogueService(
        new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());

    var snapshot = await catalogue.GetSourcesAsync(PlexApiClientTests.Server(), "2");

    Assert.True(snapshot.IsStale);
    Assert.Equal("collection", Assert.Single(snapshot.Data).Kind);
    Assert.Equal("Plex request failed.", snapshot.Error);
    Assert.DoesNotContain("secret", snapshot.Error!);
}

[Fact]
public async Task ScopedSources_RetainHubsWhenCollectionsFail()
{
    using var handler = new FakePlexHandler(request => request.RequestUri!.AbsolutePath switch
    {
        "/library/sections/2/collections" => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("secret upstream body") },
        "/hubs/sections/2" => PlexApiClientTests.Xml(
            """<MediaContainer><Hub hubIdentifier="recent" key="/hubs/recent" title="Recent" type="show"/></MediaContainer>"""),
        _ => PlexApiClientTests.Xml("<MediaContainer/>")
    });
    var catalogue = new PlexCatalogueService(
        new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());

    var snapshot = await catalogue.GetSourcesAsync(PlexApiClientTests.Server(), "2");

    Assert.True(snapshot.IsStale);
    Assert.Equal("hub", Assert.Single(snapshot.Data).Kind);
    Assert.Equal("Plex authorization failed; reconnect or update the server token.", snapshot.Error);
    Assert.DoesNotContain("secret", snapshot.Error!);
}

[Fact]
public async Task GlobalSources_RequestOnlyGlobalHubs()
{
    using var handler = new FakePlexHandler(_ => PlexApiClientTests.Xml(
        """<MediaContainer><Hub hubIdentifier="recent" key="/hubs/recent" title="Recent" type="movie"/></MediaContainer>"""));
    var catalogue = new PlexCatalogueService(
        new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());

    var snapshot = await catalogue.GetSourcesAsync(PlexApiClientTests.Server(), null);

    Assert.False(snapshot.IsStale);
    Assert.Equal("hub", Assert.Single(snapshot.Data).Kind);
    Assert.Equal(new[] { "/hubs" }, handler.Requests.Select(request => request.Uri.AbsolutePath));
}
```

- [ ] **Step 2: Run the catalogue tests and verify RED**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~PlexCatalogueServiceTests' \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: the two partial-result tests FAIL because the current combined fetch discards all data after either component fails. The global characterization test passes.

- [ ] **Step 3: Split source projection in `PlexApiClient`**

Replace the combined `GetSourcesAsync` implementation with independent methods:

```csharp
public async Task<IReadOnlyList<PlexSource>> GetCollectionsAsync(PlexServer server, string libraryId,
    CancellationToken ct = default)
{
    var collections = await ReadPagesAsync(server,
        $"/library/sections/{Identifier(libraryId)}/collections", 2000, ["Directory"], ct).ConfigureAwait(false);
    return collections.Select(item => new PlexSource(server.Id, libraryId, "collection",
        Attribute(item, "ratingKey") ?? Attribute(item, "key") ?? "",
        Attribute(item, "key") ?? $"/library/collections/{Identifier(Attribute(item, "ratingKey") ?? "")}/children",
        Attribute(item, "title") ?? "", Attribute(item, "type") ?? ""))
        .Where(source => source.Id.Length > 0 && IsSafeSourceKey(source.Key)).ToArray();
}

public async Task<IReadOnlyList<PlexSource>> GetHubsAsync(PlexServer server, string? libraryId,
    CancellationToken ct = default)
{
    var path = string.IsNullOrEmpty(libraryId) ? "/hubs" : $"/hubs/sections/{Identifier(libraryId)}";
    var hubs = await ReadPagesAsync(server, path, 2000, ["Hub"], ct).ConfigureAwait(false);
    return hubs.Select(item => new PlexSource(server.Id, libraryId, "hub",
        Attribute(item, "hubIdentifier") ?? Attribute(item, "key") ?? "", Attribute(item, "key") ?? "",
        Attribute(item, "title") ?? "", Attribute(item, "type") ?? ""))
        .Where(source => source.Id.Length > 0 && IsSafeSourceKey(source.Key)).ToArray();
}
```

Update `PlexApiClientTests.LibrariesUsersSourcesAndEpisodes_UseTypedBoundedXmlContracts` to call and concatenate the two independent methods:

```csharp
var sources = (await api.GetCollectionsAsync(Server(), "2"))
    .Concat(await api.GetHubsAsync(Server(), "2")).ToArray();
```

- [ ] **Step 4: Aggregate independent cached snapshots in `PlexCatalogueService`**

Replace `GetSourcesAsync` with:

```csharp
public async Task<PlexSnapshot<PlexSource>> GetSourcesAsync(PlexServer server, string? libraryId,
    bool forceRefresh = false, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(libraryId))
        return await GetAsync(server, "hubs", forceRefresh,
            token => api.GetHubsAsync(server, null, token), ct).ConfigureAwait(false);

    var collectionsTask = GetAsync(server, "collections:" + libraryId, forceRefresh,
        token => api.GetCollectionsAsync(server, libraryId, token), ct);
    var hubsTask = GetAsync(server, "hubs:" + libraryId, forceRefresh,
        token => api.GetHubsAsync(server, libraryId, token), ct);
    await Task.WhenAll(collectionsTask, hubsTask).ConfigureAwait(false);
    var collections = await collectionsTask.ConfigureAwait(false);
    var hubs = await hubsTask.ConfigureAwait(false);
    var errors = new[] { collections.Error, hubs.Error }
        .Where(error => !string.IsNullOrEmpty(error)).Distinct(StringComparer.Ordinal).ToArray();
    return new PlexSnapshot<PlexSource>(
        [.. collections.Data, .. hubs.Data],
        new[] { collections.LastSuccess, hubs.LastSuccess }.Max(),
        collections.IsStale || hubs.IsStale,
        errors.Length == 0 ? null : string.Join(" · ", errors));
}
```

- [ ] **Step 5: Run the API and catalogue tests and verify GREEN**

Run:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~PlexApiClientTests|FullyQualifiedName~PlexCatalogueServiceTests' \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: all selected tests PASS with zero failures.

- [ ] **Step 6: Commit resilient snapshot aggregation**

```bash
git add backend/Services/Plex/PlexApiClient.cs backend/Services/Plex/PlexCatalogueService.cs \
  tests/NzbWebDAV.Tests/Plex/PlexApiClientTests.cs tests/NzbWebDAV.Tests/Plex/PlexCatalogueServiceTests.cs
git commit -m "fix(plex): preserve partial scoped source results"
```

### Task 3: Final Verification and Handoff

**Files:**
- Verify: `backend/Services/Plex/PlexApiClient.cs`
- Verify: `backend/Services/Plex/PlexCatalogueService.cs`
- Verify: `tests/NzbWebDAV.Tests/Plex/PlexApiClientTests.cs`
- Verify: `tests/NzbWebDAV.Tests/Plex/PlexCatalogueServiceTests.cs`

- [ ] **Step 1: Run formatting and diff validation**

```bash
dotnet format tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj --no-restore --verify-no-changes
git diff --check origin/main...HEAD
```

Expected: both commands exit 0 with no formatting or whitespace errors.

- [ ] **Step 2: Rebuild and run the complete Plex-focused test set**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~Plex' \
  -p:RapidYencRuntimeIdentifier=linux-x64
```

Expected: all Plex tests PASS with zero failures.

- [ ] **Step 3: Verify repository scope and history**

```bash
git status --short
git diff --stat origin/main...HEAD
git log --oneline origin/main..HEAD
```

Expected: only the design, plan, two Plex service files, and two Plex test files differ from `origin/main`; the worktree has no uncommitted product changes after committing the plan.

- [ ] **Step 4: Push and open the issue-linked pull request**

```bash
git push -u origin fix/plex-scoped-source-discovery
gh pr create --repo johoja12/infinidysk --base main --head fix/plex-scoped-source-discovery \
  --title "fix(plex): restore library hubs and collections in Smart Prefetch" \
  --body $'Closes #16\n\n## Summary\n- use the supported Plex scoped-hubs endpoint\n- preserve collections or hubs when the other catalogue request fails\n- add exact-path and partial-result regression coverage\n\n## Test plan\n- focused Plex API and catalogue tests\n- complete Plex-focused backend test set\n- dotnet format verification'
```

Expected: the pushed branch head matches the local commit and the returned PR URL begins with `https://github.com/johoja12/infinidysk/`.
