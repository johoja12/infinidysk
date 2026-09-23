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

    [Fact]
    public async Task Browse_GroupsAcrossPagesBeforePaginatingAndKeepsAllMappings()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var names = new[] { "show-a-s01e01.mkv", "movie-b.mkv", "show-a-s01e02.mkv" };
        for (var i = 0; i < ids.Length; i++)
        {
            var item = DavItem.New(ids[i], DavItem.ContentFolder, names[i], 100,
                DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
                null, null, null, null);
            _context.Items.Add(item);
            _context.LinkMaps.Add(new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id,
                LinkPath = i == 1 ? "Movies/Movie B/Movie B.mkv" : $"TV Shows/Show A/Season 01/{names[i]}",
                TargetText = $"/mnt/.ids/{item.Id}.mkv", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        }
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), DavItemId = ids[0], LinkPath = "TV Shows/Show A/alternate.mkv",
            TargetText = $"/mnt/.ids/{ids[0]}.mkv", MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
        });
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), LinkPath = "unclassified/external.mkv", TargetText = "/nas/external.mkv",
            MappingType = LibraryMappingType.External, Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryCatalogService(_context);
        var first = await service.BrowseAsync(new LibraryCatalogQuery { Page = 1, PageSize = 1 });
        Assert.Equal(3, first.TotalGroups);
        Assert.Equal(4, first.TotalFiles);
        Assert.Equal(1, first.ShowCount);
        var show = Assert.Single(first.Groups);
        Assert.Equal("show", show.Kind);
        Assert.Equal(2, show.FileCount);
        Assert.Equal(3, show.MappingCount);
        Assert.Equal(2, show.Files.Count);
        Assert.Contains(show.Files, file => file.EpisodeLabel == "S01E01");

        var filtered = await service.BrowseAsync(new LibraryCatalogQuery
        {
            Search = "Show A", TypeFilter = "internal", PageSize = 1,
        });
        Assert.Equal(1, filtered.TotalGroups);
        Assert.Equal(2, filtered.TotalFiles);
        Assert.Equal(2, Assert.Single(filtered.Groups).Files.Count);
    }
}
