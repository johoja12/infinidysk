# Library File Modal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/library` rows clickable and open a file modal with Preview, Download, health-check, requeue-repair, and prewarm actions.

**Architecture:** One new additive GET endpoint (`/api/get-library-file-details`) serves DavItem fields plus latest health result plus index mappings; health history gains an optional `davItemId` filter; the frontend adds a `LibraryFileModal` component reusing `Modal`, `MediaPreview`, and health-route action patterns.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core, xUnit, React Router 8 + Vitest, OpenAPI contract workflow (`scripts/export-admin-openapi.sh`, `npm run generate:api` in `frontend/`).

---

## File structure

New backend files (all under `backend/`, root namespace `NzbWebDAV`):

- `Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsRequest.cs` — parses and validates `davItemId`.
- `Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsResponse.cs` — item fields + latest health + mappings DTO.
- `Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsController.cs` — GET-only controller, API-key auth inherited.
- `contracts/admin/v1/get-library-file-details.schema.json` — response JSON schema.

Modified backend files:

- `Api/Controllers/GetHealthCheckHistory/GetHealthCheckHistoryRequest.cs` — optional `davItemId` UUID filter.
- `Api/Controllers/GetHealthCheckHistory/GetHealthCheckHistoryController.cs` — apply the filter to `itemsQuery`.
- `Api/OpenApi/AdminApiContractCatalog.cs` — bump `ContractVersion` to `"2.3.0"`, append the new GET operation.
- `contracts/openapi/admin-v1.json` — regenerated via `scripts/export-admin-openapi.sh`.

New frontend files:

- `frontend/app/routes/library/file-modal.tsx` — `LibraryFileModal` component (details + actions + mappings + history).
- `frontend/app/routes/library/file-modal.test.tsx` — component tests (open, actions, empty states).

Modified frontend files:

- `frontend/app/routes/library/route.tsx` — clickable rows, modal state, details fetch, actions wiring.
- `frontend/app/routes/library/route.test.ts` — loader tests stay; add modal interaction coverage only if the route owns state (otherwise covered in `file-modal.test.tsx`).
- `frontend/app/clients/admin-operations.ts` — operation entry + `adminApi.libraryFileDetails`.
- `frontend/app/clients/backend-client.server.ts` — zod schemas, `LibraryFileDetails*` types, `getLibraryFileDetails`, `davItemId` on `getHealthCheckHistory`, `requeueActionNeededHealthChecks(davItemId?)`, `warmPrefetch(itemIds)`.
- `frontend/app/clients/backend-client.server.test.ts` — client tests.

Docs:

- `docs/features/media-library.md` — modal + actions section with `since` pill (confirm version from `version.txt` at implementation time; repo is at 1.4.2, catalog doc uses `[since 1.5.0]`).

---

### Task 1: `davItemId` filter on health history (backend)

**Files:**
- Modify: `backend/Api/Controllers/GetHealthCheckHistory/GetHealthCheckHistoryRequest.cs`
- Modify: `backend/Api/Controllers/GetHealthCheckHistory/GetHealthCheckHistoryController.cs`
- Test: `tests/NzbWebDAV.Tests/Api/AdminContractTests.cs` (append new fact)

- [ ] **Step 1: Write the failing test**

Append to `tests/NzbWebDAV.Tests/Api/AdminContractTests.cs` (reuse existing usings: `DavItem`, `NzbDavWebApplicationFactory`, `JsonDocument`, `HttpStatusCode`):

```csharp
[Fact]
public async Task HealthCheckHistory_DavItemIdFilter_ReturnsOnlyMatchingRows()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    var wanted = Guid.NewGuid();
    var other = Guid.NewGuid();
    await factory.AddDavItemsAsync(
        DavItem.New(wanted, DavItem.ContentFolder, "wanted.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null),
        DavItem.New(other, DavItem.ContentFolder, "other.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null));
    await factory.AddHealthCheckResultsAsync(
        new HealthCheckResult { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, DavItemId = wanted, Path = "/content/wanted.mkv", Result = HealthCheckResult.HealthResult.Healthy, RepairStatus = HealthCheckResult.RepairAction.None },
        new HealthCheckResult { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, DavItemId = other, Path = "/content/other.mkv", Result = HealthCheckResult.HealthResult.Healthy, RepairStatus = HealthCheckResult.RepairAction.None });

    using var response = await client.GetAsync($"/api/get-health-check-history?davItemId={wanted:D}&pageSize=50");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
    Assert.NotEmpty(items);
    Assert.All(items, row =>
        Assert.Equal(wanted.ToString("D"), row.GetProperty("davItemId").GetString()));
}

[Fact]
public async Task HealthCheckHistory_InvalidDavItemId_ReturnsBadRequest()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.GetAsync("/api/get-health-check-history?davItemId=not-a-guid");
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}
```

Note for the worker: verify `AddHealthCheckResultsAsync` exists on the factory first (grep `AddDavItemsAsync` in tests to find the factory helpers). If it does not exist, seed via `factory.Db`/`dbClient.Ctx.HealthCheckResults.AddRange` following whatever seeding helper `AddDavItemsAsync` uses, then `SaveChangesAsync`. Also verify the JSON property name for the item id (`davItemId` vs `id`) by reading `GetHealthCheckHistoryResponse` serialization in the controller before finalizing the assertions.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests.HealthCheckHistory_DavItemIdFilter|FullyQualifiedName~AdminContractTests.HealthCheckHistory_InvalidDavItemId" --nologo -p:SkipRapidYencNativeEnsure=true`
Expected: FAIL (unknown query param is ignored, so the filter test returns both rows; invalid-guid test returns 200).

- [ ] **Step 3: Write minimal implementation**

In `GetHealthCheckHistoryRequest.cs`, add the property and parsing (after the `resultParam` block, before `errors.ThrowIfAny()`):

```csharp
public Guid? DavItemId { get; init; }

// inside the constructor, after the resultParam block:
var davItemIdParam = context.GetQueryParam("davItemId");
if (davItemIdParam is not null)
{
    if (!Guid.TryParse(davItemIdParam, out var davItemId))
        errors.Add("davItemId", "Invalid davItemId parameter (use a UUID).");
    else
        DavItemId = davItemId;
}
```

In `GetHealthCheckHistoryController.cs`, after the existing `Results` filter block and before `var totalCount`:

```csharp
if (request.DavItemId is Guid davItemId)
    itemsQuery = itemsQuery.Where(x => x.DavItemId == davItemId);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests.HealthCheckHistory" --nologo -p:SkipRapidYencNativeEnsure=true`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/Api/Controllers/GetHealthCheckHistory tests/NzbWebDAV.Tests/Api/AdminContractTests.cs
git commit -m "feat(health): filter health history by dav item id"
```

---

### Task 2: GET /api/get-library-file-details (backend)

**Files:**
- Create: `backend/Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsRequest.cs`
- Create: `backend/Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsResponse.cs`
- Create: `backend/Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsController.cs`
- Create: `contracts/admin/v1/get-library-file-details.schema.json`
- Test: `tests/NzbWebDAV.Tests/Api/AdminContractTests.cs` (append facts)

- [ ] **Step 1: Write the failing tests**

Append to `tests/NzbWebDAV.Tests/Api/AdminContractTests.cs`:

```csharp
[Fact]
public async Task LibraryFileDetails_ReturnsItemWithMappingsAndLatestHealth()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    var id = Guid.NewGuid();
    await factory.AddDavItemsAsync(
        DavItem.New(id, DavItem.ContentFolder, "detail-film.mkv", 2048,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null));

    using var response = await client.GetAsync($"/api/get-library-file-details?davItemId={id:D}");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    JsonContractValidator.AssertMatchesSchema(
        json.RootElement, "admin/v1/get-library-file-details.schema.json");
    Assert.Equal(id.ToString("D"), json.RootElement.GetProperty("davItemId").GetString());
    Assert.Equal("detail-film.mkv", json.RootElement.GetProperty("name").GetString());
}

[Fact]
public async Task LibraryFileDetails_MissingItem_ReturnsNotFound()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.GetAsync($"/api/get-library-file-details?davItemId={Guid.NewGuid():D}");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task LibraryFileDetails_RequiresApiKey()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var anonymous = factory.CreateClient();

    using var response = await anonymous.GetAsync($"/api/get-library-file-details?davItemId={Guid.NewGuid():D}");
    await AdminProblemAssertions.AssertProblemAsync(
        response, HttpStatusCode.Unauthorized, "API Key Required");
}

[Fact]
public async Task LibraryFileDetails_InvalidDavItemId_ReturnsBadRequest()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    using var response = await client.GetAsync("/api/get-library-file-details?davItemId=nope");
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests.LibraryFileDetails" --nologo -p:SkipRapidYencNativeEnsure=true`
Expected: FAIL with 404 (no such route; the MissingItem test passes vacuously — that is fine, it guards the final behavior).

- [ ] **Step 3: Write minimal implementation**

`backend/Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsRequest.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetLibraryFileDetails;

public class GetLibraryFileDetailsRequest
{
    public Guid DavItemId { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public GetLibraryFileDetailsRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();
        var raw = context.GetQueryParam("davItemId");
        if (!Guid.TryParse(raw, out var davItemId))
            errors.Add("davItemId", "Invalid davItemId parameter (use a UUID).");
        else
            DavItemId = davItemId;
        errors.ThrowIfAny();
    }
}
```

`backend/Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsResponse.cs`:

```csharp
namespace NzbWebDAV.Api.Controllers.GetLibraryFileDetails;

public class GetLibraryFileDetailsResponse : BaseApiResponse
{
    public required string DavItemId { get; init; }
    public required string Name { get; init; }
    public required string ContentPath { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public DateTimeOffset? LastHealthCheck { get; init; }
    public DateTimeOffset? NextHealthCheck { get; init; }
    public bool HealthRepairPending { get; init; }
    public string? HistoryItemId { get; init; }
    public LibraryFileDetailsHealth? LatestHealth { get; init; }
    public List<LibraryFileDetailsMapping> Mappings { get; init; } = [];

    public class LibraryFileDetailsHealth
    {
        public required string Result { get; init; }
        public required string RepairStatus { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    public class LibraryFileDetailsMapping
    {
        public required string LinkPath { get; init; }
        public required string TargetText { get; init; }
        public required string MappingType { get; init; }
        public required string Status { get; init; }
    }
}
```

`backend/Api/Controllers/GetLibraryFileDetails/GetLibraryFileDetailsController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetLibraryFileDetails;

[ApiController]
[Route("api/get-library-file-details")]
public class GetLibraryFileDetailsController(DavDatabaseClient dbClient) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetLibraryFileDetailsRequest(HttpContext);
        var ct = request.CancellationToken;

        var item = await dbClient.Ctx.Items.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.DavItemId, ct)
            .ConfigureAwait(false);
        if (item is null)
            return NotFound(new BaseApiResponse { Status = false, Error = "Item not found." });

        var latest = await dbClient.Ctx.HealthCheckResults.AsNoTracking()
            .Where(x => x.DavItemId == request.DavItemId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var mappings = await dbClient.Ctx.LinkMaps.AsNoTracking()
            .Where(m => m.DavItemId == request.DavItemId)
            .OrderBy(m => m.LinkPath)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(new GetLibraryFileDetailsResponse
        {
            DavItemId = item.Id.ToString("D"),
            Name = item.Name,
            ContentPath = item.Path,
            Size = item.FileSize,
            ReleaseDate = item.ReleaseDate,
            LastHealthCheck = item.LastHealthCheck,
            NextHealthCheck = item.NextHealthCheck,
            HealthRepairPending = item.HealthRepairPending,
            HistoryItemId = item.HistoryItemId?.ToString("D"),
            LatestHealth = latest is null ? null : new GetLibraryFileDetailsResponse.LibraryFileDetailsHealth
            {
                Result = latest.Result.ToString().ToLowerInvariant(),
                RepairStatus = latest.RepairStatus.ToString().ToLowerInvariant(),
                Message = latest.Message,
                CreatedAt = latest.CreatedAt,
            },
            Mappings = mappings.Select(m => new GetLibraryFileDetailsResponse.LibraryFileDetailsMapping
            {
                LinkPath = m.LinkPath,
                TargetText = m.TargetText,
                MappingType = m.MappingType.ToString().ToLowerInvariant(),
                Status = m.Status.ToString().ToLowerInvariant(),
            }).ToList(),
        });
    }
}
```

Notes for the worker: verify enum member names on `HealthCheckResult.HealthResult` / `RepairAction` (check `backend/Database/Models/HealthCheckResult.cs`) so the lowercased strings match the frontend's expectations; adjust the mapping if members differ. If `GetOnlyApiController` rejects non-GET or anonymous callers differently than the catalog controller, mirror `GetLibraryCatalogController` exactly (same base class, same constructor shape). The 404 path returns a plain `NotFound` object — check how other GET controllers return 404 (grep `NotFound(` under `backend/Api/Controllers`) and match the dominant pattern; `BaseApiController` catches `ArgumentException` as 400, but a missing row is not an argument error.

`contracts/admin/v1/get-library-file-details.schema.json`:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://infinidysk.local/contracts/admin/v1/get-library-file-details.schema.json",
  "title": "Admin get-library-file-details success",
  "type": "object",
  "required": ["davItemId", "name", "contentPath", "mappings"],
  "properties": {
    "davItemId": { "type": "string" },
    "name": { "type": "string" },
    "contentPath": { "type": "string" },
    "size": { "type": ["integer", "null"] },
    "releaseDate": { "type": ["string", "null"] },
    "lastHealthCheck": { "type": ["string", "null"] },
    "nextHealthCheck": { "type": ["string", "null"] },
    "healthRepairPending": { "type": "boolean" },
    "historyItemId": { "type": ["string", "null"] },
    "latestHealth": {
      "type": ["object", "null"],
      "required": ["result", "repairStatus", "createdAt"],
      "properties": {
        "result": { "type": "string" },
        "repairStatus": { "type": "string" },
        "message": { "type": ["string", "null"] },
        "createdAt": { "type": "string" }
      }
    },
    "mappings": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["linkPath", "targetText", "mappingType", "status"],
        "properties": {
          "linkPath": { "type": "string" },
          "targetText": { "type": "string" },
          "mappingType": { "type": "string", "enum": ["internal", "external"] },
          "status": { "type": "string", "enum": ["valid", "broken", "unchecked", "stale"] }
        }
      }
    }
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests.LibraryFileDetails" --nologo -p:SkipRapidYencNativeEnsure=true`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add backend/Api/Controllers/GetLibraryFileDetails contracts/admin/v1/get-library-file-details.schema.json tests/NzbWebDAV.Tests/Api/AdminContractTests.cs
git commit -m "feat(library): add per-file library details API"
```

---

### Task 3: Contract version bump + OpenAPI regen

**Files:**
- Modify: `backend/Api/OpenApi/AdminApiContractCatalog.cs`
- Modify (generated): `contracts/openapi/admin-v1.json`
- Test: existing `AdminOpenApiIntegrationTests` + `AdminContractTests`

- [ ] **Step 1: Bump the catalog**

In `backend/Api/OpenApi/AdminApiContractCatalog.cs`: change `ContractVersion` from `"2.2.0"` to `"2.3.0"` and append to `FrontendOperations`:

```csharp
new("GET", "/api/get-library-file-details", "get-api-get-library-file-details"),
```

- [ ] **Step 2: Regenerate the committed contract**

Run: `bash scripts/export-admin-openapi.sh` from the repo root.
Expected: `contracts/openapi/admin-v1.json` updated with the new path; no other diff.

- [ ] **Step 3: Verify compat + contract tests**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests|FullyQualifiedName~AdminOpenApiIntegrationTests" --nologo -p:SkipRapidYencNativeEnsure=true`
Expected: PASS. Also run `python3 scripts/check-admin-openapi-compat.py --current contracts/openapi/admin-v1.json`; the additive GET must not report a breaking change.

- [ ] **Step 4: Commit**

```bash
git add backend/Api/OpenApi contracts/openapi/admin-v1.json
git commit -m "feat(api): publish library file details contract v2.3.0"
```

---

### Task 4: Frontend client wiring + generated types

**Files:**
- Modify: `frontend/app/clients/admin-operations.ts`
- Modify: `frontend/app/clients/backend-client.server.ts`
- Modify: `frontend/app/clients/backend-client.server.test.ts`

- [ ] **Step 1: Write the failing tests**

Append to `frontend/app/clients/backend-client.server.test.ts` (follow the existing `getLibraryCatalog` test: `fetchMock.mockResolvedValueOnce(jsonResponse(...))`, then assert URL + method + `x-api-key` header):

```ts
it("gets library file details by dav item id", async () => {
  fetchMock.mockResolvedValueOnce(
    jsonResponse({
      davItemId: "11111111-1111-1111-1111-111111111111",
      name: "detail-film.mkv",
      contentPath: "/content/detail-film.mkv",
      mappings: [],
    }),
  );

  await backendClient.getLibraryFileDetails("11111111-1111-1111-1111-111111111111");

  const [url, init] = fetchMock.mock.calls[0]!;
  expect(url).toBe(
    "http://backend/api/get-library-file-details?davItemId=11111111-1111-1111-1111-111111111111",
  );
  expect(init?.method).toBe("GET");
  expect(init?.headers).toEqual({ "x-api-key": "test-api-key" });
});

it("passes davItemId through to health history", async () => {
  fetchMock.mockResolvedValueOnce(jsonResponse({ stats: [], items: [], totalCount: 0 }));

  await backendClient.getHealthCheckHistory({
    davItemId: "11111111-1111-1111-1111-111111111111",
    pageSize: 5,
  });

  const [url] = fetchMock.mock.calls[0]!;
  expect(url).toContain("davItemId=11111111-1111-1111-1111-111111111111");
});

it("requeues a single item by dav item id", async () => {
  fetchMock.mockResolvedValueOnce(jsonResponse({ requeuedCount: 1 }));

  await backendClient.requeueActionNeededHealthChecks("11111111-1111-1111-1111-111111111111");

  const [url, init] = fetchMock.mock.calls[0]!;
  expect(url).toBe(
    "http://backend/api/requeue-action-needed-health-checks?davItemId=11111111-1111-1111-1111-111111111111",
  );
  expect(init?.method).toBe("POST");
});

it("warms prefetch items by id", async () => {
  fetchMock.mockResolvedValueOnce(jsonResponse({ status: true }));

  await backendClient.warmPrefetch(["11111111-1111-1111-1111-111111111111"]);

  const [url, init] = fetchMock.mock.calls[0]!;
  expect(url).toBe("http://backend/api/prefetch/operations");
  expect(init?.method).toBe("POST");
  expect(JSON.parse(init?.body as string)).toMatchObject({
    Operation: "warm",
    ItemIds: ["11111111-1111-1111-1111-111111111111"],
  });
});
```

Note for the worker: verify the exact `jsonResponse` helper name and `fetchMock` access pattern in the existing test file before writing; the `getLibraryCatalog` test is the template. For the warm test, verify the controller's JSON binding names (`OperationRequest(string Operation, Guid[]? ItemIds, ...)`) — System.Text.Json default binding in this repo; check how other POST JSON bodies are serialized in `backend-client.server.ts` (camelCase vs PascalCase) and match it.

- [ ] **Step 2: Run test to verify it fails**

Run from `frontend/`: `NODE_ENV=development npx vitest run app/clients/backend-client.server.test.ts`
Expected: FAIL (`getLibraryFileDetails is not a function` and friends).

- [ ] **Step 3: Write minimal implementation**

In `admin-operations.ts`, add to `adminFrontendOperations`:

```ts
{
  method: "get",
  path: "/api/get-library-file-details",
  operationId: "get-api-get-library-file-details",
},
```

and to `adminApi`:

```ts
libraryFileDetails: "/api/get-library-file-details",
prefetchOperations: "/api/prefetch/operations",
```

Check whether `adminApi` already has a prefetch entry; if so, reuse it instead of adding a duplicate.

In `backend-client.server.ts`, add schemas + types (next to the library catalog schemas):

```ts
const libraryFileDetailsHealthSchema = z.object({
  result: z.string(),
  repairStatus: z.string(),
  message: z.string().nullable().optional(),
  createdAt: z.string(),
});

const libraryFileDetailsResponseSchema = z.object({
  davItemId: z.string(),
  name: z.string(),
  contentPath: z.string(),
  size: z.number().nullable().optional(),
  releaseDate: z.string().nullable().optional(),
  lastHealthCheck: z.string().nullable().optional(),
  nextHealthCheck: z.string().nullable().optional(),
  healthRepairPending: z.boolean().optional(),
  historyItemId: z.string().nullable().optional(),
  latestHealth: libraryFileDetailsHealthSchema.nullable().optional(),
  mappings: z.array(libraryCatalogMappingSchema),
});

export type LibraryFileDetails = z.infer<typeof libraryFileDetailsResponseSchema>;
```

Methods on `BackendClient`:

```ts
public async getLibraryFileDetails(davItemId: string): Promise<LibraryFileDetails> {
  return await call<LibraryFileDetails>(
    `${adminApi.libraryFileDetails}?davItemId=${encodeURIComponent(davItemId)}`,
    "Failed to get library file details",
    { method: "GET" },
    libraryFileDetailsResponseSchema,
  );
}

public async warmPrefetch(itemIds: string[]): Promise<void> {
  await call(
    adminApi.prefetchOperations,
    "Failed to warm prefetch items",
    {
      method: "POST",
      body: JSON.stringify({ Operation: "warm", ItemIds: itemIds }),
      headers: { "Content-Type": "application/json" },
    },
  );
}
```

Check the `call` helper signature for how JSON POST bodies + extra headers are passed elsewhere in this file and match that convention (the sketch above may need adjusting to the helper's options shape).

Extend `requeueActionNeededHealthChecks` with an optional id:

```ts
public async requeueActionNeededHealthChecks(davItemId?: string): Promise<{ requeuedCount: number }> {
  const query = davItemId ? `?davItemId=${encodeURIComponent(davItemId)}` : "";
  return await call<{ requeuedCount: number }>(
    `${adminApi.requeueActionNeededHealthChecks}${query}`,
    "Failed to requeue action-needed health checks",
    {
      method: "POST",
    },
  );
}
```

Add `davItemId?: string` to `GetHealthCheckHistoryParams` and `if (params.davItemId) qs.set("davItemId", params.davItemId);` in `getHealthCheckHistory`.

Then regenerate types from `frontend/`: `NODE_ENV=development npm run generate:api`. Expect no hand-written changes beyond the regen output.

- [ ] **Step 4: Run test to verify it passes**

Run from `frontend/`: `NODE_ENV=development npx vitest run app/clients/backend-client.server.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend/app/clients frontend/app/generated
git commit -m "feat(library): wire library file details client"
```

---

### Task 5: LibraryFileModal component

**Files:**
- Create: `frontend/app/routes/library/file-modal.tsx`
- Create: `frontend/app/routes/library/file-modal.test.tsx`

- [ ] **Step 1: Write the failing tests**

`frontend/app/routes/library/file-modal.test.tsx`:

```tsx
import { describe, expect, it, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { LibraryFileModal } from "./file-modal";
import type { LibraryCatalogItem } from "~/clients/backend-client.server";

vi.mock("~/routes/explore/media-preview/media-preview", () => ({
  MediaPreview: ({ fileName, onClose }: { fileName: string; onClose: () => void }) => (
    <div>
      <span>preview:{fileName}</span>
      <button onClick={onClose}>close-preview</button>
    </div>
  ),
}));

const item: LibraryCatalogItem = {
  kind: "internal",
  davItemId: "11111111-1111-1111-1111-111111111111",
  displayName: "detail-film.mkv",
  contentPath: "/content/detail-film.mkv",
  size: 2048,
  mappingCount: 1,
  health: "healthy",
  mappings: [
    { linkPath: "movies/detail-film.mkv", targetText: "/mnt/.ids/x.mkv", mappingType: "internal", status: "valid" },
  ],
};

describe("LibraryFileModal", () => {
  it("renders file facts, mappings, and empty health state", () => {
    render(
      <LibraryFileModal
        item={item}
        details={null}
        detailsLoading={false}
        detailsError={null}
        previewUrl="/view/content/detail-film.mkv?downloadKey=k"
        canPrewarm={true}
        actionState="idle"
        feedback={null}
        onClose={() => {}}
        onPreview={() => {}}
        onRunHealthCheck={() => {}}
        onRequeue={() => {}}
        onPrewarm={() => {}}
      />,
    );
    expect(screen.getByText("detail-film.mkv")).toBeTruthy();
    expect(screen.getByText("movies/detail-film.mkv")).toBeTruthy();
    expect(screen.getByText(/no health checks recorded/i)).toBeTruthy();
  });

  it("opens preview and fires modal actions", () => {
    const onPreview = vi.fn();
    const onRequeue = vi.fn();
    render(
      <LibraryFileModal
        item={item}
        details={null}
        detailsLoading={false}
        detailsError={null}
        previewUrl="/view/content/detail-film.mkv?downloadKey=k"
        canPrewarm={true}
        actionState="idle"
        feedback={null}
        onClose={() => {}}
        onPreview={onPreview}
        onRunHealthCheck={() => {}}
        onRequeue={onRequeue}
        onPrewarm={() => {}}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /preview/i }));
    fireEvent.click(screen.getByRole("button", { name: /requeue/i }));
    expect(onPreview).toHaveBeenCalledTimes(1);
    expect(onRequeue).toHaveBeenCalledTimes(1);
  });

  it("hides prewarm when native cache is inactive", () => {
    render(
      <LibraryFileModal
        item={item}
        details={null}
        detailsLoading={false}
        detailsError={null}
        previewUrl="/view/content/detail-film.mkv?downloadKey=k"
        canPrewarm={false}
        actionState="idle"
        feedback={null}
        onClose={() => {}}
        onPreview={() => {}}
        onRunHealthCheck={() => {}}
        onRequeue={() => {}}
        onPrewarm={() => {}}
      />,
    );
    expect(screen.queryByRole("button", { name: /prewarm/i })).toBeNull();
    expect(screen.getByText(/native cache is inactive/i)).toBeTruthy();
  });
});
```

Notes for the worker: verify `@testing-library/react` is available in `frontend/` (the health route has `route.component.test.tsx` — copy its render/assert imports). Verify the `LibraryCatalogItem` type's required fields match the fixture (read the zod schema in `backend-client.server.ts`). If `MediaPreview`'s module path or props differ, adjust the mock. The `details` prop type is `LibraryFileDetails | null`; when `details` is present the modal shows latest-health + extra facts, otherwise it falls back to catalog-row data.

- [ ] **Step 2: Run test to verify it fails**

Run from `frontend/`: `NODE_ENV=development npx vitest run app/routes/library/file-modal.test.tsx`
Expected: FAIL (`file-modal` module not found).

- [ ] **Step 3: Write minimal implementation**

`frontend/app/routes/library/file-modal.tsx` — presentational component only (all data + callbacks come from props so it stays testable without router context):

```tsx
import { Alert, Badge, Button, Icon, Modal } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import type {
  LibraryCatalogItem,
  LibraryFileDetails,
} from "~/clients/backend-client.server";

export type LibraryModalActionState = "idle" | "pending" | "error";

export type LibraryModalFeedback = {
  variant: "success" | "danger";
  message: string;
} | null;

export type LibraryFileModalProps = {
  item: LibraryCatalogItem;
  details: LibraryFileDetails | null;
  detailsLoading: boolean;
  detailsError: string | null;
  previewUrl: string | null;
  canPrewarm: boolean;
  actionState: LibraryModalActionState;
  feedback: LibraryModalFeedback;
  onClose: () => void;
  onPreview: () => void;
  onRunHealthCheck: () => void;
  onRequeue: () => void;
  onPrewarm: () => void;
};

export function LibraryFileModal(props: LibraryFileModalProps) {
  const { item, details, detailsLoading, detailsError, previewUrl, canPrewarm } = props;
  const latest = details?.latestHealth ?? null;
  const downloadUrl = previewUrl ? `${previewUrl}${previewUrl.includes("?") ? "&" : "?"}download=true` : null;

  return (
    <Modal open title={item.displayName} onClose={props.onClose} size="wide">
      <div className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2">
          <Badge>{item.health}</Badge>
          {item.size != null && <span className="font-mono text-xs">{formatFileSize(item.size)}</span>}
          <span className="break-all font-mono text-xs text-base-content/60">
            {item.contentPath ?? item.mappings[0]?.targetText ?? "—"}
          </span>
        </div>

        {detailsLoading && <p role="status">Loading file details…</p>}
        {detailsError && (
          <Alert variant="danger" role="alert">
            {detailsError}
          </Alert>
        )}

        <div className="flex flex-wrap gap-2">
          {previewUrl && (
            <Button size="small" onClick={props.onPreview} disabled={props.actionState === "pending"}>
              <Icon name="play_arrow" className="!text-[16px]" />
              Preview
            </Button>
          )}
          {downloadUrl && (
            <a className="btn btn-sm gap-2" href={downloadUrl}>
              <Icon name="download" className="!text-[16px]" />
              Download
            </a>
          )}
          {item.kind === "internal" && item.davItemId && (
            <>
              <Button
                variant="outline"
                size="small"
                onClick={props.onRunHealthCheck}
                disabled={props.actionState === "pending"}
              >
                <Icon name="favorite" className="!text-[16px]" />
                Run health check
              </Button>
              <Button
                variant="outline"
                size="small"
                onClick={props.onRequeue}
                disabled={props.actionState === "pending"}
              >
                <Icon name="refresh" className="!text-[16px]" />
                Requeue repair
              </Button>
              {canPrewarm ? (
                <Button
                  variant="outline"
                  size="small"
                  onClick={props.onPrewarm}
                  disabled={props.actionState === "pending"}
                >
                  <Icon name="bolt" className="!text-[16px]" />
                  Prewarm
                </Button>
              ) : null}
            </>
          )}
        </div>
        {!canPrewarm && item.kind === "internal" && (
          <p className="text-xs text-base-content/60">
            Prewarm is unavailable: Native cache is inactive.
          </p>
        )}
        <p className="text-xs text-base-content/60">
          Run health check queues all due checks, not just this file.
        </p>

        {props.feedback && (
          <Alert variant={props.feedback.variant} role={props.feedback.variant === "danger" ? "alert" : "status"}>
            {props.feedback.message}
          </Alert>
        )}

        <div>
          <h3 className="text-sm font-semibold">Mappings ({item.mappingCount})</h3>
          <ul className="mt-1 flex flex-col gap-1">
            {item.mappings.map((m) => (
              <li key={m.linkPath} className="flex flex-wrap items-center gap-2 text-sm">
                <Badge>{m.mappingType}</Badge>
                <Badge>{m.status}</Badge>
                <code className="break-all">
                  {m.linkPath} → {m.targetText}
                </code>
              </li>
            ))}
          </ul>
        </div>

        <div>
          <h3 className="text-sm font-semibold">Health history</h3>
          {latest ? (
            <p className="text-sm">
              {latest.result} · {latest.repairStatus} ·{" "}
              {new Date(latest.createdAt).toLocaleString()}
              {latest.message ? ` — ${latest.message}` : ""}
            </p>
          ) : (
            <p className="text-sm text-base-content/60">No health checks recorded for this file.</p>
          )}
          <a className="link text-sm" href="/health">
            Open Health
          </a>
        </div>
      </div>
    </Modal>
  );
}
```

Notes for the worker: verify `Badge`, `Button`, `Icon`, `Alert`, `Modal` prop names against `frontend/app/components/ui/` (they exist per the explore preview + health route usage: `Modal open title onClose size`, `Button variant size onClick disabled`, `Alert variant role`). Verify icon names exist in the `Icon` set (grep `frontend/app/components/ui/icon.tsx` for `play_arrow`, `download`, `favorite`, `refresh`, `bolt`; substitute existing names if any are missing). Preview rendering itself is owned by the route (Task 6), not this component — the component only fires `onPreview`. The modal must never close on action failure; `onClose` is only wired to the dialog dismiss.

- [ ] **Step 4: Run test to verify it passes**

Run from `frontend/`: `NODE_ENV=development npx vitest run app/routes/library/file-modal.test.tsx`
Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add frontend/app/routes/library/file-modal.tsx frontend/app/routes/library/file-modal.test.tsx
git commit -m "feat(library): add library file modal component"
```

---

### Task 6: Clickable rows + modal wiring in the library route

**Files:**
- Modify: `frontend/app/routes/library/route.tsx`
- Modify: `frontend/app/routes/library/route.test.ts`

- [ ] **Step 1: Write the failing test**

Add to `frontend/app/routes/library/route.test.ts` a loader test proving the loader also supplies a signed preview URL base per internal row is NOT needed — instead, test the new loader field. First extend the loader (Step 3), then assert: extend the existing `catalogMock().mockResolvedValue` items with one internal row and assert `loaderData.previewUrls[davItemId]` starts with `/view/`. Concretely, append:

```ts
it("builds signed preview urls for internal rows", async () => {
  catalogMock().mockResolvedValue({
    items: [
      {
        kind: "internal",
        davItemId: "11111111-1111-1111-1111-111111111111",
        displayName: "film.mkv",
        contentPath: "/content/film.mkv",
        size: 100,
        mappingCount: 1,
        health: "healthy",
        mappings: [],
      },
    ],
    totalCount: 1,
    page: 1,
    pageSize: 25,
  });

  const data = await loader({ request: requestFor("/library"), params: {} } as never);

  expect(data.previewUrls["11111111-1111-1111-1111-111111111111"]).toMatch(
    /^\/view\/content\/film\.mkv\?downloadKey=.+/,
  );
});
```

Write this test first and watch it fail (`previewUrls` undefined), then implement.

- [ ] **Step 2: Run test to verify it fails**

Run from `frontend/`: `NODE_ENV=development npx vitest run app/routes/library/route.test.ts`
Expected: FAIL (`previewUrls` does not exist on loader data).

- [ ] **Step 3: Write minimal implementation**

Loader changes in `route.tsx`:

1. Extend `LibraryPageData` with `previewUrls: Record<string, string>`.
2. Build it next to `downloadKeys`: for each internal item with `contentPath` + `davItemId`, `previewUrls[davItemId] = `/view${contentPath}?downloadKey=${key}`` (reuse the same `getDownloadKey` call already made for `downloadKeys`).
3. Return `{ query, catalog, downloadKeys, previewUrls }`.

Component changes in `route.tsx`:

1. Track state: `const [selectedId, setSelectedId] = useState<string | null>(null)`, `const [details, setDetails] = useState<LibraryFileDetails | null>(null)`, `detailsLoading/detailsError`, `showPreview`, `actionState`, `feedback`.
2. Make each name cell a `<button type="button" className="link" aria-haspopup="dialog" onClick={() => openModal(item)}>` that sets `selectedId` and fires a `fetch` to the new resource route (see below) for details. Keep the `<details>` mappings row untouched.
3. Render `<LibraryFileModal>` when `selectedId` is set, passing the catalog item, details state, `previewUrls[selectedId]`, action callbacks, and `onClose` clearing state.
4. Render `MediaPreview` (import from `~/routes/explore/media-preview/media-preview`) when `showPreview` is true, with `fileName`, `filePath`, `mimeType` (guess from extension: `.mkv` → `video/x-matroska`, `.mp4` → `video/mp4`, else `application/octet-stream`), `sizeBytes`, `previewUrl`, `onClose`.
5. Action callbacks use `fetch` + `withUrlBase` exactly like `health/route.tsx` does:
   - Run health check → `POST /api/trigger-health-check`; 409 shows the body error; success feedback "Health checks queued." Mirror `onRunAllChecks` copy.
   - Requeue → `POST /api/requeue-action-needed-health-checks?davItemId=`; feedback mirrors `onRequeueActionNeeded` copy.
   - Prewarm → needs a tiny resource route action (React Router `action` in `route.tsx` calling `backendClient.warmPrefetch`) because prefetch requires the API key server-side; the client component calls `useFetcher().submit({ davItemId }, { method: "post" })`. `canPrewarm` comes from loader data — add a `nativeCacheActive` boolean to the loader via `backendClient` native-cache status call (check what `NativeCacheController` GET returns: `ActiveMode`; treat anything other than `inactive`/`off` as active — verify field values first). If that call is expensive, wrap in try/catch defaulting to `false`.
6. Details fetch: add a React Router `action`? No — reads stay in `loader`-adjacent code. Fetch details client-side via a new lightweight resource route `frontend/app/routes/library.details/route.ts`? Simpler: reuse the existing frontend proxy — the browser can `fetch("/api/get-library-file-details?davItemId=...")` directly since `frontend/server/app.ts` forwards `/api` with the API key injected for authenticated sessions (verify this proxy behavior in `frontend/server/app.ts` before committing to it; if it does not forward GET admin calls, add a `clientLoader` using `backendClient.getLibraryFileDetails` instead and pass through route state).

Pick whichever of the two details-fetch paths matches the codebase's established pattern (grep how `health/route.tsx` or `explore/route.tsx` does authenticated client-side fetches; `withUrlBase("/api/...")` in health suggests direct `/api` fetch works — prefer that).

- [ ] **Step 4: Run tests + typecheck + lint**

Run from `frontend/`:
- `NODE_ENV=development npx vitest run app/routes/library/`
- `NODE_ENV=development npm run typecheck`
- `NODE_ENV=development npm run lint -- app/routes/library/`

Expected: PASS, 0 errors, no new lint findings.

- [ ] **Step 5: Commit**

```bash
git add frontend/app/routes/library/route.tsx frontend/app/routes/library/route.test.ts
git commit -m "feat(library): open file modal from catalog rows"
```

---

### Task 7: Docs + final verification

**Files:**
- Modify: `docs/features/media-library.md`
- Verify: full backend + frontend + contract suites

- [ ] **Step 1: Document the modal**

In `docs/features/media-library.md`, add a `## File details modal` section after the catalog description: clickable rows, Preview/Download/Run health check/Requeue repair/Prewarm actions with the whole-queue disclosure for health checks and the Native-cache-inactive note for prewarm, mappings list, latest health history with link to Health. Use the same `since` pill version as the page's existing pill (confirm from `version.txt` at implementation time).

- [ ] **Step 2: Run the full verification set**

Backend from repo root: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests|FullyQualifiedName~AdminOpenApiIntegrationTests|FullyQualifiedName~LibraryCatalogServiceTests|FullyQualifiedName~LibraryCatalogScannerTests|FullyQualifiedName~LibraryLinkMapTests|FullyQualifiedName~LibraryLinkClassifierTests" --nologo -p:SkipRapidYencNativeEnsure=true`
Frontend from `frontend/`: `NODE_ENV=development npx vitest run app/routes/library/ app/clients/backend-client.server.test.ts`, `NODE_ENV=development npm run typecheck`, `NODE_ENV=development npm run lint`
Contract: `python3 scripts/check-admin-openapi-compat.py --current contracts/openapi/admin-v1.json`
Docs: `zensical build --clean --strict` (only if docs changed beyond the single section — it did, so run it).

- [ ] **Step 3: Commit**

```bash
git add docs/features/media-library.md
git commit -m "docs(library): document file details modal"
```

---

## Self-review

- Spec coverage: row interaction → Task 6; modal sections (facts/actions/mappings/history) → Tasks 5+6; new details endpoint → Task 2; history filter → Task 1; contract bump/schemas → Task 3; error handling → Tasks 5+6 (inline Alert, never close on failure); tests → every task; docs → Task 7. External-row mappings-only modal → Task 5 (`item.kind === "internal"` gates the action bar) + Task 6 (no details fetch for external rows — worker must skip the fetch when `davItemId` is null).
- Placeholders: none — every step has concrete code/commands; two clearly-marked worker verification points (factory seeding helper, proxy fetch pattern) resolve by reading named files, not by invention.
- Type consistency: `LibraryFileDetails` zod type flows Task 4 → Task 5 props → Task 6 state; `davItemId` UUID string used uniformly; `Operation: "warm"` matches `PrefetchOperationController.OperationRequest`.
