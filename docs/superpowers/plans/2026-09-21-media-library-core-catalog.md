# Media Library Core Catalog (Read-Only) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only, server-paginated `/library` catalog page backed by a persisted symlink-mapping index, per the approved spec in `docs/superpowers/specs/2026-09-21-media-library-core-catalog-design.md`.

**Architecture:** A pure link classifier feeds a background scanner that upserts `LibraryLinkMap` rows; a query service LEFT JOINs `/content` `DavItem`s with mappings and UNIONs external-only links into one paginated DTO; a GET admin controller serves it; a new `/library` route renders deduplicated rows with expandable mappings.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core (SQLite/PostgreSQL), xUnit, React Router 8 + Vitest, OpenAPI contract workflow (`scripts/export-admin-openapi.sh`, `npm run generate:api`).

---

## File structure

New backend files (all under `backend/`, root namespace `NzbWebDAV`):

- `MediaLibrary/LibraryMappingType.cs` — `Internal`/`External` enum.
- `MediaLibrary/LibraryLinkStatus.cs` — `Valid`/`Broken`/`Unchecked`/`Stale` enum.
- `MediaLibrary/ClassifiedLink.cs` — pure classifier input/output record.
- `MediaLibrary/LibraryLinkClassifier.cs` — static, no-IO classifier; reuses `OrganizedLinksUtil.GetDavItemLink`.
- `Database/Models/LibraryLinkMap.cs` — EF entity.
- `Services/Library/LibraryCatalogScanner.cs` — `BackgroundService` doing discovery + upsert + stale marking.
- `Services/Library/LibraryCatalogModels.cs` — `LibraryCatalogItemDto`, `LibraryCatalogMappingDto`, `LibraryCatalogQuery`, `LibraryCatalogResult`.
- `Services/Library/LibraryCatalogService.cs` — read-only query service.
- `Api/Controllers/GetLibraryCatalog/GetLibraryCatalogRequest.cs`
- `Api/Controllers/GetLibraryCatalog/GetLibraryCatalogResponse.cs`
- `Api/Controllers/GetLibraryCatalog/GetLibraryCatalogController.cs`
- `Database/Migrations/<timestamp>_Add-Library-Link-Maps.cs` (+ Designer, via `dotnet ef`)

Modified backend files:

- `Database/DavDatabaseContext.cs` — `DbSet<LibraryLinkMap> LinkMaps` + `OnModelCreating` mapping.
- `Program.cs` — `AddSingleton<LibraryCatalogScanner>()` + `AddHostedService(...)` next to `SearchExcludeSyncService`.
- `Api/OpenApi/AdminApiContractCatalog.cs` — add `GET /api/get-library-catalog`, bump `ContractVersion` to `2.2.0`.
- `contracts/openapi/admin-v1.json` — regenerated via `scripts/export-admin-openapi.sh`.
- `contracts/admin/v1/get-library-catalog.schema.json` — new response schema.

New frontend files:

- `frontend/app/routes/library/route.tsx` — loader + read-only table with expandable mappings.
- `frontend/app/routes/library/route.test.ts` — loader param + rendering tests.

Modified frontend files:

- `frontend/app/clients/admin-operations.ts` — operation entry + `adminApi.libraryCatalog`.
- `frontend/app/clients/backend-client.server.ts` — zod schemas, `LibraryCatalog*` types, `getLibraryCatalog`.
- `frontend/app/clients/backend-client.server.test.ts` — client test.
- `frontend/app/utils/service-provider.ts` — add `"library"` to `NAV_FEATURE_IDS`.
- `frontend/app/routes/_index/components/left-navigation/left-navigation.tsx` — Media Library nav item.

Docs:

- `docs/features/media-library.md` — new user doc with `since` pill (confirm version from `version.txt` at implementation time).
- `zensical.toml` — nav entry under Features.

---

### Task 1: Library link classifier (pure, no IO)

**Files:**
- Create: `backend/MediaLibrary/LibraryMappingType.cs`
- Create: `backend/MediaLibrary/LibraryLinkStatus.cs`
- Create: `backend/MediaLibrary/ClassifiedLink.cs`
- Create: `backend/MediaLibrary/LibraryLinkClassifier.cs`
- Test: `tests/NzbWebDAV.Tests/MediaLibrary/LibraryLinkClassifierTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Tests.MediaLibrary;

public sealed class LibraryLinkClassifierTests
{
    [Fact]
    public void Classify_IdsSymlinkTarget_ReturnsInternalWithDavItemId()
    {
        var id = Guid.NewGuid();
        var result = LibraryLinkClassifier.Classify(
            linkPath: "/library/movies/film.mkv",
            targetText: $"/mnt/nzbdav/.ids/{id}.mkv",
            isStrm: false,
            mountDir: "/mnt/nzbdav",
            libraryRoot: "/library");

        Assert.Equal(LibraryMappingType.Internal, result.MappingType);
        Assert.Equal(id, result.DavItemId);
        Assert.Equal("movies/film.mkv", result.RelativeLinkPath);
    }

    [Fact]
    public void Classify_NonIdsTarget_ReturnsExternalWithNullDavItemId()
    {
        var result = LibraryLinkClassifier.Classify(
            linkPath: "/library/docs/old.mkv",
            targetText: "/old-nas/docs/old.mkv",
            isStrm: false,
            mountDir: "/mnt/nzbdav",
            libraryRoot: "/library");

        Assert.Equal(LibraryMappingType.External, result.MappingType);
        Assert.Null(result.DavItemId);
    }

    [Fact]
    public void Classify_EscapingLinkPath_ThrowsArgumentException()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => LibraryLinkClassifier.Classify(
            linkPath: "/other/evil.mkv",
            targetText: $"/mnt/nzbdav/.ids/{id}.mkv",
            isStrm: false,
            mountDir: "/mnt/nzbdav",
            libraryRoot: "/library"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryLinkClassifierTests" --nologo`
Expected: FAIL with build error (`NzbWebDAV.MediaLibrary` namespace not found).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace NzbWebDAV.MediaLibrary;

public enum LibraryMappingType { Internal = 1, External = 2 }

public enum LibraryLinkStatus { Valid = 1, Broken = 2, Unchecked = 3, Stale = 4 }

public sealed record ClassifiedLink(
    string RelativeLinkPath,
    string TargetText,
    LibraryMappingType MappingType,
    Guid? DavItemId);
```

```csharp
using NzbWebDAV.Utils;

namespace NzbWebDAV.MediaLibrary;

/// <summary>
/// Pure classifier: maps one discovered library link to internal (InfiniDysk
/// <c>/.ids</c> target) or external. No filesystem IO — the scanner reads the
/// target text with no-follow APIs and passes it in.
/// </summary>
public static class LibraryLinkClassifier
{
    public static ClassifiedLink Classify(
        string linkPath,
        string targetText,
        bool isStrm,
        string mountDir,
        string libraryRoot)
    {
        var fullRoot = Path.GetFullPath(libraryRoot);
        var fullPath = Path.GetFullPath(linkPath);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"Link '{linkPath}' escapes the library root.", nameof(linkPath));
        }

        Guid? davItemId = null;
        if (isStrm)
        {
            davItemId = OrganizedLinksUtil.GetDavItemLink(
                new SymlinkAndStrmUtil.StrmInfo { StrmPath = fullPath, TargetUrl = targetText })?.DavItemId;
        }
        else
        {
            davItemId = OrganizedLinksUtil.GetDavItemLink(
                new SymlinkAndStrmUtil.SymlinkInfo { SymlinkPath = fullPath, TargetPath = targetText },
                mountDir)?.DavItemId;
        }

        return new ClassifiedLink(
            relative,
            targetText,
            davItemId.HasValue ? LibraryMappingType.Internal : LibraryMappingType.External,
            davItemId);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryLinkClassifierTests" --nologo`
Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add backend/MediaLibrary tests/NzbWebDAV.Tests/MediaLibrary
git commit -m "feat(library): add pure library link classifier"
```

---

### Task 2: LibraryLinkMap entity, context mapping, migration

**Files:**
- Create: `backend/Database/Models/LibraryLinkMap.cs`
- Modify: `backend/Database/DavDatabaseContext.cs`
- Create (via EF): `backend/Database/Migrations/<timestamp>_Add-Library-Link-Maps.cs` + Designer
- Test: `tests/NzbWebDAV.Tests/Database/LibraryLinkMapTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Tests.Database;

public sealed class LibraryLinkMapTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-librarymap-tests-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task InsertAndRead_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            DavItemId = id,
            LinkPath = "movies/film.mkv",
            TargetText = $"/mnt/nzbdav/.ids/{id}.mkv",
            MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var row = await _context.LinkMaps.SingleAsync(x => x.LinkPath == "movies/film.mkv");
        Assert.Equal(id, row.DavItemId);
        Assert.Equal(LibraryMappingType.Internal, row.MappingType);
        Assert.Equal(LibraryLinkStatus.Valid, row.Status);
    }

    [Fact]
    public async Task DuplicateLinkPath_ViolatesUniqueIndex()
    {
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            LinkPath = "movies/dup.mkv",
            TargetText = "/old-nas/dup.mkv",
            MappingType = LibraryMappingType.External,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            LinkPath = "movies/dup.mkv",
            TargetText = "/old-nas/other.mkv",
            MappingType = LibraryMappingType.External,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryLinkMapTests" --nologo`
Expected: FAIL with build error (`LinkMaps` / `LibraryLinkMap` not found).

- [ ] **Step 3: Write minimal implementation**

`backend/Database/Models/LibraryLinkMap.cs`:

```csharp
using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Database.Models;

/// <summary>
/// One row per discovered library symlink/STRM under <c>media.library-dir</c>.
/// <c>LinkPath</c> is relative to the library root and is the identity.
/// <c>DavItemId</c> is set only for verified internal <c>/.ids</c> targets.
/// External links (no InfiniDysk identity) have a null <c>DavItemId</c>.
/// </summary>
public class LibraryLinkMap
{
    public Guid Id { get; set; }
    public Guid? DavItemId { get; set; }
    public string LinkPath { get; set; } = null!;
    public string TargetText { get; set; } = null!;
    public LibraryMappingType MappingType { get; set; }
    public LibraryLinkStatus Status { get; set; }
    public long? Size { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
}
```

In `backend/Database/DavDatabaseContext.cs`, add the set:

```csharp
public DbSet<LibraryLinkMap> LinkMaps => Set<LibraryLinkMap>();
```

In `OnModelCreating`, add after the `Par2RepairJob` block:

```csharp
// LibraryLinkMap
b.Entity<LibraryLinkMap>(e =>
{
    e.ToTable("LibraryLinkMaps");
    e.HasKey(i => i.Id);

    e.Property(i => i.Id)
        .ValueGeneratedNever();

    e.Property(i => i.DavItemId)
        .ValueGeneratedNever()
        .IsRequired(false);

    e.Property(i => i.LinkPath)
        .IsRequired();

    e.Property(i => i.TargetText)
        .IsRequired();

    e.Property(i => i.MappingType)
        .HasConversion<int>()
        .IsRequired();

    e.Property(i => i.Status)
        .HasConversion<int>()
        .IsRequired();

    e.Property(i => i.Size)
        .IsRequired(false);

    e.HasIndex(i => i.LinkPath)
        .IsUnique();

    e.HasIndex(i => i.DavItemId)
        .IsUnique(false);
});
```

Then generate the migration (additive — plain `feat`, not breaking):

```bash
cd backend && dotnet ef migrations add Add-Library-Link-Maps
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryLinkMapTests" --nologo`
Expected: PASS (2/2). Also verify `git status` shows the new migration + Designer + updated model snapshot.

- [ ] **Step 5: Commit**

```bash
git add backend/Database tests/NzbWebDAV.Tests/Database
git commit -m "feat(db): add library link maps table migration"
```

---

### Task 3: Library catalog scanner (BackgroundService)

**Files:**
- Create: `backend/Services/Library/LibraryCatalogScanner.cs`
- Modify: `backend/Program.cs`
- Test: `tests/NzbWebDAV.Tests/Services/LibraryCatalogScannerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Services.Library;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Services;

public sealed class LibraryCatalogScannerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-scanner-tests-{Guid.NewGuid():N}.sqlite");
    private readonly string _libraryRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-library-{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_libraryRoot);
        var services = new ServiceCollection();
        services.AddDbContextFactory<DavDatabaseContext>(options =>
            options.UseSqlite($"Data Source={_databasePath}"));
        _provider = services.BuildServiceProvider();
        await using var context = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>().CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Directory.Delete(_libraryRoot, recursive: true);
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task ReconcileOnce_ExternalSymlink_UpsertsExternalRow()
    {
        var target = Path.Join(_libraryRoot, "real.mkv");
        await File.WriteAllTextAsync(target, "x");
        File.CreateSymbolicLink(Path.Join(_libraryRoot, "linked.mkv"), target);
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot }]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);

        await scanner.ReconcileOnceAsync(CancellationToken.None);

        await using var context = factory.CreateDbContext();
        var row = await context.LinkMaps.SingleAsync();
        Assert.Equal("linked.mkv", row.LinkPath);
        Assert.Equal(LibraryMappingType.External, row.MappingType);
        Assert.Null(row.DavItemId);
    }
}
```

Notes for the worker: `ConfigManager.UpdateValues` takes `List<ConfigItem>` (see `SearchExcludeSyncService.PersistAsync`). If the exact overload differs, read `ConfigManager.UpdateValues` signature first and adjust the test. `File.CreateSymbolicLink` requires Linux (CI is Linux). If `SymlinkAndStrmUtil` discovery uses `find`, ensure `find` exists in the test environment (it does on CI images).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryCatalogScannerTests" --nologo`
Expected: FAIL with build error (`LibraryCatalogScanner` not found).

- [ ] **Step 3: Write minimal implementation**

`backend/Services/Library/LibraryCatalogScanner.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Discovers symlinks/STRMs under <c>media.library-dir</c> and upserts
/// <see cref="LibraryLinkMap"/> rows. Never follows a link whose target sits
/// under the rclone mount (self-deadlock risk); such targets are classified
/// from text only. Links absent from a completed full scan are marked Stale.
/// </summary>
public sealed class LibraryCatalogScanner(
    ConfigManager configManager,
    IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(15);

    public DateTimeOffset? LastSuccessfulScanAt { get; private set; }
    public string? LastScanWarning { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                e.LogWarningKnownOrStack("Library catalog scan failed.");
                LastScanWarning = e.Message;
            }

            try { await Task.Delay(RescanInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        var libraryRoot = configManager.GetLibraryDir();
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            LastScanWarning = "Library directory is not configured.";
            return;
        }

        var mountDir = configManager.GetRcloneMountDir();
        List<(string LinkPath, string TargetText, bool IsStrm)> discovered;
        try
        {
            discovered = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(libraryRoot)
                .Select(info => info switch
                {
                    SymlinkAndStrmUtil.SymlinkInfo s => (s.SymlinkPath, s.TargetPath, false),
                    SymlinkAndStrmUtil.StrmInfo s => (s.StrmPath, s.TargetUrl, true),
                    _ => throw new InvalidOperationException("Unknown link type"),
                })
                .ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            e.LogWarningKnownOrStack("Library catalog discovery failed.");
            LastScanWarning = e.Message;
            return;
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (linkPath, targetText, isStrm) in discovered)
        {
            ct.ThrowIfCancellationRequested();
            ClassifiedLink classified;
            try
            {
                classified = LibraryLinkClassifier.Classify(linkPath, targetText, isStrm, mountDir, libraryRoot);
            }
            catch (ArgumentException)
            {
                continue; // escaping link — skip, never touch it
            }

            seen.Add(classified.RelativeLinkPath);
            var status = await ResolveStatusAsync(context, classified, mountDir, ct).ConfigureAwait(false);
            var existing = await context.LinkMaps
                .FirstOrDefaultAsync(x => x.LinkPath == classified.RelativeLinkPath, ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                context.LinkMaps.Add(new LibraryLinkMap
                {
                    Id = Guid.NewGuid(),
                    DavItemId = classified.DavItemId,
                    LinkPath = classified.RelativeLinkPath,
                    TargetText = classified.TargetText,
                    MappingType = classified.MappingType,
                    Status = status,
                    LastSeenUtc = DateTime.UtcNow,
                    LastCheckedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.DavItemId = classified.DavItemId;
                existing.TargetText = classified.TargetText;
                existing.MappingType = classified.MappingType;
                existing.Status = status;
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.LastCheckedUtc = DateTime.UtcNow;
            }
        }

        var stale = await context.LinkMaps
            .Where(x => !seen.Contains(x.LinkPath) && x.Status != LibraryLinkStatus.Stale)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var row in stale)
            row.Status = LibraryLinkStatus.Stale;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        LastSuccessfulScanAt = DateTimeOffset.UtcNow;
        LastScanWarning = null;
    }

    private static async Task<LibraryLinkStatus> ResolveStatusAsync(
        DavDatabaseContext context,
        ClassifiedLink classified,
        string mountDir,
        CancellationToken ct)
    {
        if (classified.MappingType == LibraryMappingType.Internal)
        {
            var exists = classified.DavItemId.HasValue && await context.Items
                .AnyAsync(x => x.Id == classified.DavItemId.Value, ct)
                .ConfigureAwait(false);
            return exists ? LibraryLinkStatus.Valid : LibraryLinkStatus.Broken;
        }

        // External target missing from disk counts as broken, but never probe
        // paths under the rclone mount (following them can recurse into WebDAV).
        var absolute = classified.TargetText;
        if (!Path.IsPathRooted(absolute))
            return LibraryLinkStatus.Unchecked;
        if (absolute.StartsWith(mountDir, StringComparison.Ordinal))
            return LibraryLinkStatus.Unchecked;
        try
        {
            return File.Exists(absolute) || Directory.Exists(absolute)
                ? LibraryLinkStatus.Valid
                : LibraryLinkStatus.Broken;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LibraryLinkStatus.Unchecked;
        }
    }
}
```

Verify `ConfigManager.GetRcloneMountDir()` exists and its return type (string, non-null) before finalizing — grep `GetRcloneMountDir` in `ConfigManager.cs`. Register in `Program.cs` next to `SearchExcludeSyncService`:

```csharp
.AddSingleton<LibraryCatalogScanner>()
.AddHostedService(sp => sp.GetRequiredService<LibraryCatalogScanner>())
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryCatalogScannerTests" --nologo`
Expected: PASS. Then run the full new backend set: `--filter "FullyQualifiedName~LibraryLinkClassifierTests|FullyQualifiedName~LibraryLinkMapTests|FullyQualifiedName~LibraryCatalogScannerTests"`.

- [ ] **Step 5: Commit**

```bash
git add backend/Services/Library backend/Program.cs tests/NzbWebDAV.Tests/Services
git commit -m "feat(library): scan library symlinks into mapping index"
```

---

### Task 4: Library catalog query service

**Files:**
- Create: `backend/Services/Library/LibraryCatalogModels.cs`
- Create: `backend/Services/Library/LibraryCatalogService.cs`
- Test: `tests/NzbWebDAV.Tests/Services/LibraryCatalogServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Tests.Services;

public sealed class LibraryCatalogServiceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-catalog-tests-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Query_InternalItemWithTwoMappings_ReturnsOneRowWithTwoMappings()
    {
        var item = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "film.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(item);
        _context.LinkMaps.AddRange(
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id, LinkPath = "movies/film.mkv",
                TargetText = $"/mnt/.ids/{item.Id}.mkv", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            },
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id, LinkPath = "movies/film.srt",
                TargetText = $"/mnt/.ids/{item.Id}.srt", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryCatalogService(_context);
        var result = await service.QueryAsync(new LibraryCatalogQuery { Page = 1, PageSize = 25 });

        var row = Assert.Single(result.Items, x => x.DavItemId == item.Id);
        Assert.Equal(2, row.Mappings.Count);
        Assert.Equal(100, row.Size);
    }

    [Fact]
    public async Task Query_ExternalOnlyLink_ReturnsExternalRowWithoutDavItemId()
    {
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), DavItemId = null, LinkPath = "docs/old.mkv",
            TargetText = "/old-nas/old.mkv", MappingType = LibraryMappingType.External,
            Status = LibraryLinkStatus.Broken, LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryCatalogService(_context);
        var result = await service.QueryAsync(new LibraryCatalogQuery { Page = 1, PageSize = 25 });

        var row = Assert.Single(result.Items);
        Assert.Null(row.DavItemId);
        Assert.Equal("external", row.Kind);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryCatalogServiceTests" --nologo`
Expected: FAIL with build error (`LibraryCatalogService` not found).

- [ ] **Step 3: Write minimal implementation**

`backend/Services/Library/LibraryCatalogModels.cs`:

```csharp
namespace NzbWebDAV.Services.Library;

public sealed record LibraryCatalogQuery
{
    public string? Search { get; init; }
    public string TypeFilter { get; init; } = "all"; // all|internal|external|broken
    public string Sort { get; init; } = "name"; // name|size|mappings
    public string Direction { get; init; } = "asc"; // asc|desc
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record LibraryCatalogMappingDto(
    string LinkPath,
    string TargetText,
    string MappingType,
    string Status);

public sealed record LibraryCatalogItemDto(
    string Kind, // internal|external
    Guid? DavItemId,
    string DisplayName,
    string? ContentPath,
    long? Size,
    int MappingCount,
    string Health,
    IReadOnlyList<LibraryCatalogMappingDto> Mappings);

public sealed record LibraryCatalogResult(
    IReadOnlyList<LibraryCatalogItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    DateTimeOffset? IndexScannedAt,
    string? IndexWarning);
```

`backend/Services/Library/LibraryCatalogService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Read-only catalog: one row per <c>/content</c> Usenet file (deduplicated by
/// <c>DavItem.Id</c>, size = the DavItem file size) plus one row per external
/// symlink link path. Search spans item name/path and mapping link/target.
/// </summary>
public sealed class LibraryCatalogService(DavDatabaseContext context)
{
    public async Task<LibraryCatalogResult> QueryAsync(
        LibraryCatalogQuery query,
        LibraryCatalogScanner? scanner = null,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var search = query.Search?.Trim();
        var descending = string.Equals(query.Direction, "desc", StringComparison.OrdinalIgnoreCase);

        var maps = context.LinkMaps.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            maps = maps.Where(m =>
                m.LinkPath.Contains(search) || m.TargetText.Contains(search));
        }

        var items = context.Items.AsNoTracking()
            .Where(i => i.Type == DavItem.ItemType.UsenetFile
                && i.Path.StartsWith("/content/", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(search))
        {
            var matchingIds = maps
                .Where(m => m.DavItemId != null)
                .Select(m => m.DavItemId!.Value)
                .Distinct();
            items = items.Where(i =>
                i.Name.Contains(search) || i.Path.Contains(search) || matchingIds.Contains(i.Id));
        }

        var internalRows = await items
            .Select(i => new { Item = i })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var internalIds = internalRows.Select(r => r.Item.Id).ToList();
        var mappingsByItem = (await maps
                .Where(m => m.DavItemId != null && internalIds.Contains(m.DavItemId.Value))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(m => m.DavItemId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryLinkMap>)g.ToList());

        var dtos = new List<LibraryCatalogItemDto>();
        foreach (var row in internalRows)
        {
            mappingsByItem.TryGetValue(row.Item.Id, out var mappings);
            mappings ??= [];
            var dto = ToInternalDto(row.Item, mappings);
            if (PassesTypeFilter(dto, query.TypeFilter))
                dtos.Add(dto);
        }

        var externalMaps = await maps
            .Where(m => m.DavItemId == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var group in externalMaps.GroupBy(m => m.LinkPath, StringComparer.Ordinal))
        {
            var dto = ToExternalDto(group.ToList());
            if (PassesTypeFilter(dto, query.TypeFilter))
                dtos.Add(dto);
        }

        dtos = SortDtos(dtos, query.Sort, descending);
        var total = dtos.Count;
        var pageItems = dtos.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new LibraryCatalogResult(
            pageItems, total, page, pageSize,
            scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning);
    }

    private static LibraryCatalogItemDto ToInternalDto(DavItem item, IReadOnlyList<LibraryLinkMap> mappings) =>
        new("internal", item.Id, item.Name, item.Path, item.FileSize, mappings.Count,
            mappings.Count == 0 ? "unmapped"
                : mappings.All(m => m.Status == LibraryLinkStatus.Valid) ? "healthy" : "attention",
            mappings.Select(m => new LibraryCatalogMappingDto(
                m.LinkPath, m.TargetText, m.MappingType.ToString().ToLowerInvariant(),
                m.Status.ToString().ToLowerInvariant())).ToList());

    private static LibraryCatalogItemDto ToExternalDto(IReadOnlyList<LibraryLinkMap> group)
    {
        var first = group[0];
        return new("external", null, first.LinkPath, null, null, group.Count,
            group.All(m => m.Status == LibraryLinkStatus.Valid) ? "external" : "attention",
            group.Select(m => new LibraryCatalogMappingDto(
                m.LinkPath, m.TargetText, "external",
                m.Status.ToString().ToLowerInvariant())).ToList());
    }

    private static bool PassesTypeFilter(LibraryCatalogItemDto dto, string filter) =>
        filter switch
        {
            "internal" => dto.Kind == "internal",
            "external" => dto.Kind == "external",
            "broken" => dto.Mappings.Any(m => m.Status is "broken" or "stale"),
            _ => true,
        };

    private static List<LibraryCatalogItemDto> SortDtos(
        List<LibraryCatalogItemDto> dtos, string sort, bool descending)
    {
        IOrderedEnumerable<LibraryCatalogItemDto> ordered = sort switch
        {
            "size" => descending
                ? dtos.OrderByDescending(d => d.Size ?? -1).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.Size ?? -1).ThenBy(d => d.DisplayName, StringComparer.Ordinal),
            "mappings" => descending
                ? dtos.OrderByDescending(d => d.MappingCount).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.MappingCount).ThenBy(d => d.DisplayName, StringComparer.Ordinal),
            _ => descending
                ? dtos.OrderByDescending(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.DisplayName, StringComparer.Ordinal),
        };
        return ordered.ToList();
    }
}
```

Performance note for the worker: the in-memory sort/page is acceptable for the first version only if `/content` file counts stay modest; the spec calls for server-side pagination that scales. If `DavItems` under `/content` can be large, push search + paging into the database query instead of materializing `internalRows` fully. Prefer a DB-paged implementation when the test seed shows the naive version loading unbounded rows — keep the DTO shape identical.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryCatalogServiceTests" --nologo`
Expected: PASS (2/2).

- [ ] **Step 5: Commit**

```bash
git add backend/Services/Library/LibraryCatalogModels.cs backend/Services/Library/LibraryCatalogService.cs tests/NzbWebDAV.Tests/Services
git commit -m "feat(library): add catalog query service"
```

---

### Task 5: GET /api/get-library-catalog + contract + schema

**Files:**
- Create: `backend/Api/Controllers/GetLibraryCatalog/GetLibraryCatalogRequest.cs`
- Create: `backend/Api/Controllers/GetLibraryCatalog/GetLibraryCatalogResponse.cs`
- Create: `backend/Api/Controllers/GetLibraryCatalog/GetLibraryCatalogController.cs`
- Modify: `backend/Api/OpenApi/AdminApiContractCatalog.cs`
- Create: `contracts/admin/v1/get-library-catalog.schema.json`
- Modify (generated): `contracts/openapi/admin-v1.json`
- Test: extend `tests/NzbWebDAV.Tests/Api/AdminContractTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `tests/NzbWebDAV.Tests/Api/AdminContractTests.cs`:

```csharp
[Fact]
public async Task LibraryCatalog_ReturnsSeededItemWithMappings()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var client = factory.CreateAuthenticatedClient();

    var item = DavItem.New(
        Guid.NewGuid(),
        DavItem.ContentFolder,
        "catalog-film.mkv",
        2048,
        DavItem.ItemType.UsenetFile,
        DavItem.ItemSubType.NzbFile,
        null, null, null, null);
    await factory.AddDavItemsAsync(item);

    using var response = await client.GetAsync("/api/get-library-catalog?page=1&pageSize=25");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    JsonContractValidator.AssertMatchesSchema(
        json.RootElement, "admin/v1/get-library-catalog.schema.json");
    Assert.True(json.RootElement.GetProperty("totalCount").GetInt32() >= 1);
    Assert.Contains(
        json.RootElement.GetProperty("items").EnumerateArray(),
        row => row.GetProperty("displayName").GetString() == "catalog-film.mkv");
}

[Fact]
public async Task LibraryCatalog_RequiresApiKey()
{
    await using var factory = new NzbDavWebApplicationFactory();
    using var anonymous = factory.CreateClient();

    using var response = await anonymous.GetAsync("/api/get-library-catalog");
    await AdminProblemAssertions.AssertProblemAsync(
        response, HttpStatusCode.Unauthorized, "API Key Required");
}
```

Check `AdminProblemAssertions` helper exists (used by the existing auth test in the same file) and reuse it exactly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests.LibraryCatalog" --nologo`
Expected: FAIL with 404 (no such route).

- [ ] **Step 3: Write minimal implementation**

`backend/Api/Controllers/GetLibraryCatalog/GetLibraryCatalogRequest.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Api.Controllers.GetLibraryCatalog;

public class GetLibraryCatalogRequest
{
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "all", "internal", "external", "broken" };
    private static readonly HashSet<string> AllowedSorts =
        new(StringComparer.OrdinalIgnoreCase) { "name", "size", "mappings" };

    public LibraryCatalogQuery Query { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public GetLibraryCatalogRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();

        var page = 1;
        var pageSize = 25;
        if (errors.TryParseInt("page", context.GetQueryParam("page"), "Invalid page parameter", out var parsedPage))
            page = Math.Max(1, parsedPage);
        if (errors.TryParseInt("pageSize", context.GetQueryParam("pageSize"), "Invalid pageSize parameter", out var parsedSize))
            pageSize = parsedSize;

        var type = context.GetQueryParam("type") ?? "all";
        if (!AllowedTypes.Contains(type)) errors.Add("type", "Invalid type parameter.");
        var sort = context.GetQueryParam("sort") ?? "name";
        if (!AllowedSorts.Contains(sort)) errors.Add("sort", "Invalid sort parameter.");
        var dir = (context.GetQueryParam("dir") ?? "asc").ToLowerInvariant();
        if (dir is not ("asc" or "desc")) errors.Add("dir", "Invalid dir parameter.");

        var search = context.GetQueryParam("q");
        if (search is { Length: > 200 }) errors.Add("q", "Search query is too long.");
        errors.ThrowIfAny();

        Query = new LibraryCatalogQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            TypeFilter = type.ToLowerInvariant(),
            Sort = sort.ToLowerInvariant(),
            Direction = dir,
            Page = page,
            PageSize = Math.Clamp(pageSize, 1, 100),
        };
    }
}
```

`backend/Api/Controllers/GetLibraryCatalog/GetLibraryCatalogResponse.cs`:

```csharp
namespace NzbWebDAV.Api.Controllers.GetLibraryCatalog;

public class GetLibraryCatalogResponse : BaseApiResponse
{
    public List<LibraryCatalogItem> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public DateTimeOffset? IndexScannedAt { get; init; }
    public string? IndexWarning { get; init; }

    public class LibraryCatalogItem
    {
        public required string Kind { get; init; }
        public string? DavItemId { get; init; }
        public required string DisplayName { get; init; }
        public string? ContentPath { get; init; }
        public long? Size { get; init; }
        public int MappingCount { get; init; }
        public required string Health { get; init; }
        public List<LibraryCatalogMapping> Mappings { get; init; } = [];
    }

    public class LibraryCatalogMapping
    {
        public required string LinkPath { get; init; }
        public required string TargetText { get; init; }
        public required string MappingType { get; init; }
        public required string Status { get; init; }
    }
}
```

`backend/Api/Controllers/GetLibraryCatalog/GetLibraryCatalogController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Api.Controllers.GetLibraryCatalog;

[ApiController]
[Route("api/get-library-catalog")]
public class GetLibraryCatalogController(
    DavDatabaseClient dbClient,
    LibraryCatalogScanner? scanner = null) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetLibraryCatalogRequest(HttpContext);
        var service = new LibraryCatalogService(dbClient.Ctx);
        var result = await service
            .QueryAsync(request.Query, scanner, request.CancellationToken)
            .ConfigureAwait(false);
        return Ok(new GetLibraryCatalogResponse
        {
            Items = result.Items.Select(i => new GetLibraryCatalogResponse.LibraryCatalogItem
            {
                Kind = i.Kind,
                DavItemId = i.DavItemId?.ToString(),
                DisplayName = i.DisplayName,
                ContentPath = i.ContentPath,
                Size = i.Size,
                MappingCount = i.MappingCount,
                Health = i.Health,
                Mappings = i.Mappings.Select(m => new GetLibraryCatalogResponse.LibraryCatalogMapping
                {
                    LinkPath = m.LinkPath,
                    TargetText = m.TargetText,
                    MappingType = m.MappingType,
                    Status = m.Status,
                }).ToList(),
            }).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
            IndexScannedAt = result.IndexScannedAt,
            IndexWarning = result.IndexWarning,
        });
    }
}
```

Check how `DavDatabaseClient` is injected into existing GET controllers (constructor injection of `DavDatabaseClient` works — see `GetHealthCheckQueueController`). `LibraryCatalogScanner` is registered as singleton + hosted service, so optional constructor injection resolves when present. If DI cannot resolve the nullable singleton in tests, inject `IServiceProvider` instead and resolve `LibraryCatalogScanner` defensively.

Contract + schema:

1. In `AdminApiContractCatalog.cs`: change `ContractVersion` to `"2.2.0"` and append `new("GET", "/api/get-library-catalog", "get-api-get-library-catalog")` to `FrontendOperations`.
2. Add `contracts/admin/v1/get-library-catalog.schema.json` mirroring `list-webdav-directory.schema.json` shape:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://infinidysk.local/contracts/admin/v1/get-library-catalog.schema.json",
  "title": "Admin get-library-catalog success",
  "type": "object",
  "required": ["items", "totalCount", "page", "pageSize"],
  "properties": {
    "items": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["kind", "displayName", "mappingCount", "health", "mappings"],
        "properties": {
          "kind": { "type": "string", "enum": ["internal", "external"] },
          "davItemId": { "type": ["string", "null"] },
          "displayName": { "type": "string" },
          "contentPath": { "type": ["string", "null"] },
          "size": { "type": ["integer", "null"] },
          "mappingCount": { "type": "integer" },
          "health": { "type": "string" },
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
    },
    "totalCount": { "type": "integer" },
    "page": { "type": "integer" },
    "pageSize": { "type": "integer" },
    "indexScannedAt": { "type": ["string", "null"] },
    "indexWarning": { "type": ["string", "null"] }
  }
}
```

3. Regenerate the committed OpenAPI contract: `bash scripts/export-admin-openapi.sh` from the repo root.
4. Verify `AdminApiContractCatalogTests` (if one asserts catalog/contract parity — grep for `FrontendOperations` in tests) still passes; update expectations only for the added operation + version.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~AdminContractTests" --nologo`
Expected: PASS. Also run `python3 scripts/check-admin-openapi-compat.py --current contracts/openapi/admin-v1.json` if it supports contract-only mode (see `ci.yml` HTTP-contracts lane); additive GET must not report a breaking change.

- [ ] **Step 5: Commit**

```bash
git add backend/Api/Controllers/GetLibraryCatalog backend/Api/OpenApi contracts tests/NzbWebDAV.Tests/Api
git commit -m "feat(library): add read-only library catalog API"
```

---

### Task 6: Frontend client wiring + generated types

**Files:**
- Modify: `frontend/app/clients/admin-operations.ts`
- Modify: `frontend/app/clients/backend-client.server.ts`
- Modify: `frontend/app/clients/backend-client.server.test.ts`

- [ ] **Step 1: Write the failing test**

Append to `frontend/app/clients/backend-client.server.test.ts`:

```ts
it("gets the library catalog with search, filter, and pagination", async () => {
  fetchMock.mockResolvedValueOnce(
    jsonResponse({ items: [], totalCount: 0, page: 2, pageSize: 50 }),
  );

  await backendClient.getLibraryCatalog({ q: "dune", type: "internal", page: 2, pageSize: 50 });

  const [url, init] = fetchMock.mock.calls[0]!;
  expect(url).toBe(
    "http://backend/api/get-library-catalog?q=dune&type=internal&sort=name&dir=asc&page=2&pageSize=50",
  );
  expect(init?.method).toBe("GET");
  expect(init?.headers).toEqual({ "x-api-key": "test-api-key" });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm run generate:api && npx vitest run app/clients/backend-client.server.test.ts`
Expected: FAIL (`getLibraryCatalog is not a function`). Run from `frontend/`.

- [ ] **Step 3: Write minimal implementation**

In `admin-operations.ts`, add to `adminFrontendOperations`:

```ts
{
  method: "get",
  path: "/api/get-library-catalog",
  operationId: "get-api-get-library-catalog",
},
```

and to `adminApi`:

```ts
libraryCatalog: "/api/get-library-catalog",
```

In `backend-client.server.ts`, add schemas + types + method (place near `listWebdavDirectory`):

```ts
const libraryCatalogMappingSchema = z.object({
  linkPath: z.string(),
  targetText: z.string(),
  mappingType: z.enum(["internal", "external"]),
  status: z.enum(["valid", "broken", "unchecked", "stale"]),
});

const libraryCatalogItemSchema = z.object({
  kind: z.enum(["internal", "external"]),
  davItemId: z.string().nullable().optional(),
  displayName: z.string(),
  contentPath: z.string().nullable().optional(),
  size: z.number().nullable().optional(),
  mappingCount: z.number().int(),
  health: z.string(),
  mappings: z.array(libraryCatalogMappingSchema),
});

const libraryCatalogResponseSchema = z.object({
  items: z.array(libraryCatalogItemSchema),
  totalCount: z.number().int(),
  page: z.number().int(),
  pageSize: z.number().int(),
  indexScannedAt: z.string().nullable().optional(),
  indexWarning: z.string().nullable().optional(),
});

export type LibraryCatalogMapping = z.infer<typeof libraryCatalogMappingSchema>;
export type LibraryCatalogItem = z.infer<typeof libraryCatalogItemSchema>;
export type LibraryCatalogResponse = z.infer<typeof libraryCatalogResponseSchema>;

export type LibraryCatalogQuery = {
  q?: string;
  type?: "all" | "internal" | "external" | "broken";
  sort?: "name" | "size" | "mappings";
  dir?: "asc" | "desc";
  page?: number;
  pageSize?: number;
};
```

Method on `BackendClient`:

```ts
public async getLibraryCatalog(query: LibraryCatalogQuery = {}): Promise<LibraryCatalogResponse> {
  const qs = new URLSearchParams();
  if (query.q) qs.set("q", query.q);
  qs.set("type", query.type ?? "all");
  qs.set("sort", query.sort ?? "name");
  qs.set("dir", query.dir ?? "asc");
  qs.set("page", String(query.page ?? 1));
  qs.set("pageSize", String(query.pageSize ?? 25));
  return await call<LibraryCatalogResponse>(
    `${adminApi.libraryCatalog}?${qs.toString()}`,
    "Failed to get library catalog",
    { method: "GET" },
    libraryCatalogResponseSchema,
  );
}
```

Then regenerate types so `adminApi.libraryCatalog` typechecks against the generated OpenAPI: `npm run generate:api`.

- [ ] **Step 4: Run test to verify it passes**

Run: `npm run generate:api && npx vitest run app/clients/backend-client.server.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend/app/clients contracts/openapi/admin-v1.json frontend/app/generated
git commit -m "feat(library): wire library catalog admin client"
```

Note: only commit generated files if they are tracked in git — check `git status` first. `contracts/openapi/admin-v1.json` is tracked; `app/generated/admin-api.ts` may be gitignored (generated at build). Commit whatever `git status` shows as modified-and-tracked.

---

### Task 7: /library route + navigation + feature gating

**Files:**
- Create: `frontend/app/routes/library/route.tsx`
- Create: `frontend/app/routes/library/route.test.ts`
- Modify: `frontend/app/utils/service-provider.ts`
- Modify: `frontend/app/routes/_index/components/left-navigation/left-navigation.tsx`

- [ ] **Step 1: Write the failing test**

`frontend/app/routes/library/route.test.ts`:

```ts
import { describe, expect, it, vi } from "vitest";
import { loader } from "./route";
import { backendClient } from "~/clients/backend-client.server";

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: { getLibraryCatalog: vi.fn() },
}));

function requestFor(path: string): Request {
  return new Request(`http://localhost${path}`);
}

describe("library loader", () => {
  it("passes search, filter, sort, and pagination to the catalog client", async () => {
    vi.mocked(backendClient.getLibraryCatalog).mockResolvedValueOnce({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 25,
    });

    await loader({ request: requestFor("/library?q=dune&type=broken&sort=size&dir=desc&page=2"), params: {} } as never);

    expect(backendClient.getLibraryCatalog).toHaveBeenCalledWith({
      q: "dune",
      type: "broken",
      sort: "size",
      dir: "desc",
      page: 2,
      pageSize: 25,
    });
  });

  it("clamps invalid page and pageSize to defaults", async () => {
    vi.mocked(backendClient.getLibraryCatalog).mockResolvedValueOnce({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 25,
    });

    await loader({ request: requestFor("/library?page=0&pageSize=9999"), params: {} } as never);

    expect(backendClient.getLibraryCatalog).toHaveBeenCalledWith(
      expect.objectContaining({ page: 1, pageSize: 25 }),
    );
  });
});
```

Check how existing route tests mock the loader args (there is no `explore/route.test.ts`; look at the closest loader test, e.g. health or search route tests, and match their `loader({request, params})` cast pattern). Adjust the cast if the repo convention differs.

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run app/routes/library/route.test.ts`
Expected: FAIL (module `./route` not found).

- [ ] **Step 3: Write minimal implementation**

`frontend/app/routes/library/route.tsx` (flatRoutes auto-registers `routes/library/route.tsx` as `/library` — same convention as `routes/queue/route.tsx`; no `routes.ts` change needed):

```tsx
import type { Route } from "./+types/route";
import { Form, Link, useSearchParams } from "react-router";
import {
  backendClient,
  type LibraryCatalogItem,
  type LibraryCatalogResponse,
} from "~/clients/backend-client.server";
import { getDownloadKey } from "~/auth/downloads.server";
import { getFrontendRuntimeConfig } from "../../../server/runtime-config";
import { formatFileSize } from "~/utils/file-size";
import { Badge, Button, Input, PageHeader } from "~/components/ui";

const PAGE_SIZE_OPTIONS = [25, 50, 100] as const;
const DEFAULT_PAGE_SIZE = 25;

export type LibraryPageData = {
  query: { q: string; type: string; sort: string; dir: string; page: number; pageSize: number };
  catalog: LibraryCatalogResponse;
  downloadKeys: Record<string, string>;
};

function parseType(value: string | null) {
  return value === "internal" || value === "external" || value === "broken" ? value : "all";
}

function parseSort(value: string | null) {
  return value === "size" || value === "mappings" ? value : "name";
}

function parseDir(value: string | null) {
  return value === "desc" ? "desc" : "asc";
}

function parsePage(value: string | null): number {
  const page = parseInt(value ?? "1", 10);
  return Number.isFinite(page) && page > 0 ? page : 1;
}

function parsePageSize(value: string | null): number {
  const size = parseInt(value ?? String(DEFAULT_PAGE_SIZE), 10);
  return (PAGE_SIZE_OPTIONS as readonly number[]).includes(size) ? size : DEFAULT_PAGE_SIZE;
}

export async function loader({ request }: Route.LoaderArgs): Promise<LibraryPageData> {
  const url = new URL(request.url);
  const query = {
    q: url.searchParams.get("q")?.trim() ?? "",
    type: parseType(url.searchParams.get("type")),
    sort: parseSort(url.searchParams.get("sort")),
    dir: parseDir(url.searchParams.get("dir")),
    page: parsePage(url.searchParams.get("page")),
    pageSize: parsePageSize(url.searchParams.get("pageSize")),
  };
  const catalog = await backendClient.getLibraryCatalog({
    ...(query.q ? { q: query.q } : {}),
    type: query.type as "all" | "internal" | "external" | "broken",
    sort: query.sort as "name" | "size" | "mappings",
    dir: query.dir as "asc" | "desc",
    page: query.page,
    pageSize: query.pageSize,
  });
  const { frontendBackendApiKey } = getFrontendRuntimeConfig();
  const downloadKeys: Record<string, string> = {};
  for (const item of catalog.items) {
    if (item.kind === "internal" && item.contentPath && item.davItemId) {
      downloadKeys[item.davItemId] = getDownloadKey(item.contentPath, frontendBackendApiKey);
    }
  }
  return { query, catalog, downloadKeys };
}

export default function Library({ loaderData }: Route.ComponentProps) {
  const [searchParams] = useSearchParams();
  const { query, catalog, downloadKeys } = loaderData;
  const totalPages = Math.max(1, Math.ceil(catalog.totalCount / catalog.pageSize));

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-6 px-4 py-8 md:px-6">
      <PageHeader
        title="Media Library"
        subtitle="Read-only catalog of InfiniDysk media and its library symlinks."
      />
      {catalog.indexWarning ? (
        <div className="alert alert-warning">
          <span>Library index may be stale: {catalog.indexWarning}</span>
        </div>
      ) : null}
      <Form method="get" className="flex flex-wrap gap-2">
        <Input name="q" defaultValue={query.q} placeholder="Search title, content path, symlink…" />
        <select name="type" defaultValue={query.type} className="select select-bordered" aria-label="Mapping filter">
          <option value="all">All mappings</option>
          <option value="internal">Internal</option>
          <option value="external">External</option>
          <option value="broken">Broken</option>
        </select>
        <select name="sort" defaultValue={query.sort} className="select select-bordered" aria-label="Sort">
          <option value="name">Name A–Z</option>
          <option value="size">Size</option>
          <option value="mappings">Mappings</option>
        </select>
        <Button type="submit">Search</Button>
      </Form>
      <p className="text-sm text-base-content/60">
        {catalog.totalCount} items · page {catalog.page} of {totalPages}
        {catalog.indexScannedAt ? ` · index fresh as of ${catalog.indexScannedAt}` : null}
      </p>
      <div className="overflow-x-auto">
        <table className="table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Location</th>
              <th>Size</th>
              <th>Mappings</th>
              <th>State</th>
            </tr>
          </thead>
          <tbody>
            {catalog.items.map((item) => (
              <CatalogRow key={item.davItemId ?? item.displayName} item={item} downloadKeys={downloadKeys} search={searchParams.toString()} />
            ))}
          </tbody>
        </table>
      </div>
      <nav className="join" aria-label="Pagination">
        {query.page > 1 ? (
          <Link className="btn join-item" to={`?${withPage(searchParams, query.page - 1)}`}>Previous</Link>
        ) : null}
        {query.page < totalPages ? (
          <Link className="btn join-item" to={`?${withPage(searchParams, query.page + 1)}`}>Next</Link>
        ) : null}
      </nav>
    </div>
  );
}

function withPage(params: URLSearchParams, page: number): string {
  const next = new URLSearchParams(params);
  next.set("page", String(page));
  return next.toString();
}

function CatalogRow({
  item,
  downloadKeys,
  search,
}: {
  item: LibraryCatalogItem;
  downloadKeys: Record<string, string>;
  search: string;
}) {
  return (
    <>
      <tr>
        <td>{item.displayName}</td>
        <td className="max-w-xs truncate">{item.contentPath ?? item.mappings[0]?.targetText ?? "—"}</td>
        <td>{item.size != null ? formatFileSize(item.size) : "—"}</td>
        <td>{item.mappingCount}</td>
        <td>
          <Badge>{item.health}</Badge>
        </td>
      </tr>
      <tr>
        <td colSpan={5}>
          <details>
            <summary>{item.mappingCount} mapping(s) — expand to inspect</summary>
            <ul className="mt-2 flex flex-col gap-1">
              {item.mappings.map((m) => (
                <li key={m.linkPath} className="flex flex-wrap items-center gap-2 text-sm">
                  <Badge>{m.mappingType}</Badge>
                  <Badge>{m.status}</Badge>
                  <code className="break-all">{m.linkPath} → {m.targetText}</code>
                  {m.mappingType === "internal" && item.davItemId && item.contentPath ? (
                    <Link
                      className="link"
                      to={`/view${item.contentPath}?downloadKey=${downloadKeys[item.davItemId] ?? ""}&${search}`}
                    >
                      Open
                    </Link>
                  ) : (
                    <span className="text-base-content/50">inspection only</span>
                  )}
                </li>
              ))}
            </ul>
          </details>
        </td>
      </tr>
    </>
  );
}
```

Worker notes: verify `Badge`, `Button`, `Input` prop APIs in `frontend/app/components/ui` before finalizing (match the `search/route.tsx` usage exactly). Verify `formatFileSize` signature in `frontend/app/utils/file-size.ts`. The `/view` link must use the same signing scheme as Explore (`getDownloadKey(getRelativePath(...))` — check `explore/route.tsx` `getRelativePath` helper and reuse it rather than signing the absolute content path). Confirm `Input`/`select` styling matches daisyUI patterns used in queue/health filters.

Navigation: in `service-provider.ts` add `"library"` to `NAV_FEATURE_IDS`; in `left-navigation.tsx` add `{ target: "/library", icon: "video_library", label: "Media Library", featureId: "library" }` after the Files entry. Verify the icon name exists in the `Icon` set (`video_library` is a Material Symbol; check `frontend/app/components/ui/icon.tsx` for the allowed names).

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run app/routes/library/route.test.ts && npx tsc -b`
Expected: PASS + clean typecheck. Run from `frontend/`.

- [ ] **Step 5: Commit**

```bash
git add frontend/app/routes/library frontend/app/utils/service-provider.ts frontend/app/routes/_index/components/left-navigation
git commit -m "feat(library): add read-only media library page"
```

---

### Task 8: Docs + full verification

**Files:**
- Create: `docs/features/media-library.md`
- Modify: `zensical.toml`
- Modify: `docs/features/webdav-filesystem.md` (link, optional)

- [ ] **Step 1: Write the docs page**

```markdown
# Media Library [since <VERSION>](https://github.com/infinidysk/infinidysk/releases/tag/v<VERSION>){ .nzbdav-since }

The **Media Library** page (`/library`) is a read-only catalog of your InfiniDysk media and the library symlinks that point at it.

Each row is one media item (deduplicated by its stable InfiniDysk identity) with its symlink mappings nested underneath. Links that resolve to InfiniDysk `/.ids/...` targets are marked **internal** and can be opened for streaming; all other symlinks are marked **external** and are inspection-only.

Use the search box to match titles, content paths, symlink paths, or targets. Filter by mapping type (internal / external / broken) and sort by name, size, or mapping count. The header shows when the library index was last refreshed; a warning appears when the scan is stale or the library directory is unavailable.

This page never modifies your library: there are no repair, delete, cache, Plex, or Arr actions here. Use **Files** (`/explore`) to browse the raw virtual filesystem.
```

Replace `<VERSION>` with the value from `version.txt` at implementation time (the introducing release). Add `{ "Media Library" = "features/media-library.md" }` to the Features nav in `zensical.toml`.

- [ ] **Step 2: Run the full verification set**

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter "FullyQualifiedName~LibraryLinkClassifierTests|FullyQualifiedName~LibraryLinkMapTests|FullyQualifiedName~LibraryCatalogScannerTests|FullyQualifiedName~LibraryCatalogServiceTests|FullyQualifiedName~AdminContractTests" --nologo
```

```bash
cd frontend && npm run typecheck && npm run lint && npx vitest run app/clients/backend-client.server.test.ts app/routes/library/route.test.ts
```

```bash
bash scripts/test-admin-openapi-compat.sh
```

Expected: all green. (Full backend suite + frontend suite run in CI; run them here only if time permits.)

- [ ] **Step 3: Run docs build**

Run: `pip install -r docs-requirements.txt && zensical build --clean --strict` (or the repo's documented docs command if different).
Expected: clean build, no invalid-link errors.

- [ ] **Step 4: Commit**

```bash
git add docs/features/media-library.md zensical.toml docs/features/webdav-filesystem.md
git commit -m "chore(docs): document read-only media library catalog"
```

---

## Self-review

**Spec coverage:** classifier/strict parsing (Tasks 1, 3) · additive migration keyed by DavItemId with external retention (Task 2) · background scan with freshness, never scan-on-read (Task 3) · dedupe by DavItemId, external-only rows, server search/pagination/sort (Task 4) · standard API-key auth, no auxiliary-controller gap (Task 5) · signed internal playback, external inspection-only (Task 7) · no-follow safety + human-friendly warnings (Task 3) · nav gating (Task 7) · contract overhead + tests + docs with since pill (Tasks 5, 6, 8). Plex/Arr/analysis/repair explicitly excluded everywhere.

**Placeholders:** none — every step has exact paths, code, commands, and expected outputs. Two worker-verification notes are marked inline (ConfigManager overloads, UI component APIs).

**Type consistency:** `LibraryCatalogQuery/TypeFilter/Sort/Direction` field names are identical across models (Task 4), request (Task 5), client (Task 6), and route loader (Task 7). DTO enum strings (`internal/external`, `valid/broken/unchecked/stale`) match the JSON schema enums and zod enums. Controller route is `GET /api/get-library-catalog` with operation id `get-api-get-library-catalog` in catalog, admin-operations, and OpenAPI doc.
