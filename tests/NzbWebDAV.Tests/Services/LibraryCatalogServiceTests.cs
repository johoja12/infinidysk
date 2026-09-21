using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
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
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
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
