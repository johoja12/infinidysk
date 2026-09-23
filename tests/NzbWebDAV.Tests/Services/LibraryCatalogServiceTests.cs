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

        var searched = await service.QueryAsync(new LibraryCatalogQuery
        {
            Search = "film.srt", Page = 1, PageSize = 25,
        });
        Assert.Equal(2, Assert.Single(searched.Items).Mappings.Count);
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
    public async Task Browse_GroupsAllEpisodesBeforePagination_AndPagesExpandedFiles()
    {
        for (var episode = 1; episode <= 60; episode++)
        {
            var item = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder,
                $"episode-{episode:00}.mkv", 100,
                DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
                null, null, null, null);
            _context.Items.Add(item);
            _context.LinkMaps.Add(new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id,
                LinkPath = $"tv/Example Show/Season 01/Example Show - S01E{episode:00}.mkv",
                TargetText = $"/mnt/.ids/{item.Id}.mkv",
                MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        }
        var other = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "other.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(other);
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), DavItemId = other.Id,
            LinkPath = "tv/Other Show/Season 01/Other Show - S01E01.mkv",
            TargetText = $"/mnt/.ids/{other.Id}.mkv", MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryBrowseService(new LibraryCatalogService(_context));
        var first = await service.QueryAsync(new LibraryBrowseQuery
        {
            Category = "shows", PageSize = 1, GroupKey = "shows/Example Show",
        });

        Assert.Equal(2, first.TotalGroups);
        Assert.Equal(61, first.TotalItems);
        Assert.Equal(60, first.Groups[0].ItemCount);
        Assert.NotNull(first.ExpandedGroup);
        Assert.Equal(60, first.ExpandedGroup.TotalItems);
        Assert.Equal(50, first.ExpandedGroup.Items.Count);
        Assert.Equal("S01E01", first.ExpandedGroup.Items[0].Episode);
        Assert.Single(first.ExpandedGroup.Items[0].Item.Mappings);

        var second = await service.QueryAsync(new LibraryBrowseQuery
        {
            Category = "shows", PageSize = 1, GroupKey = "shows/Example Show", GroupPage = 2,
        });
        Assert.Equal(10, second.ExpandedGroup!.Items.Count);
        Assert.Equal("S01E51", second.ExpandedGroup.Items[0].Episode);
    }

    [Fact]
    public async Task Browse_ClassifiesKnownMovieFolder_ButKeepsExternalAndUnknownVisible()
    {
        var film = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "arrival.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(film);
        _context.LinkMaps.AddRange(
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = film.Id,
                LinkPath = "Movies/Arrival (2016)/Arrival.mkv",
                TargetText = $"/mnt/.ids/{film.Id}.mkv", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            },
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = null,
                LinkPath = "TV/External Show/Season 01/Episode.mkv",
                TargetText = "/other/Episode.mkv", MappingType = LibraryMappingType.External,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryBrowseService(new LibraryCatalogService(_context));
        var movies = await service.QueryAsync(new LibraryBrowseQuery { Category = "movies" });
        var movie = Assert.Single(movies.Groups);
        Assert.Equal("Arrival (2016)", movie.Title);
        Assert.Equal(2, movies.TotalItems);
        Assert.Equal(1, movies.UnmatchedItems);

        var unmatched = await service.QueryAsync(new LibraryBrowseQuery
        {
            Category = "unmatched", TypeFilter = "external",
            Search = "External Show",
        });
        Assert.Single(unmatched.Groups);
        Assert.Equal(1, unmatched.TotalItems);
        Assert.Equal(1, unmatched.UnmatchedItems);
    }

    [Fact]
    public async Task Browse_ClassifiesSuffixedLibraryRoots_AndLeavesUnknownRootsUnmatched()
    {
        var paths = new[]
        {
            "TV-4K/All Her Fault/Season 1/All Her Fault - S01E01.mkv",
            "TV-HD/Star Trek - Voyager/Season 4/Star Trek - Voyager - S04E22.avi",
            "TV-Kids/The Amazing World of Gumball/Season 6/Gumball - S06E37.mp4",
            "Movies-4K/Ash (2025)/Ash (2025).mkv",
            "Movies-HD/Project X (2012)/Project X (2012).mkv",
            "TVExtras/Unknown Show/Season 1/Unknown Show - S01E01.mkv",
        };
        foreach (var path in paths)
        {
            var item = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder,
                Path.GetFileName(path), 100, DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile, null, null, null, null);
            _context.Items.Add(item);
            _context.LinkMaps.Add(new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id, LinkPath = path,
                TargetText = $"/mnt/.ids/{item.Id}",
                MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryBrowseService(new LibraryCatalogService(_context));
        var shows = await service.QueryAsync(new LibraryBrowseQuery { Category = "shows" });
        var movies = await service.QueryAsync(new LibraryBrowseQuery { Category = "movies" });
        var unmatched = await service.QueryAsync(new LibraryBrowseQuery { Category = "unmatched" });

        Assert.Equal(3, shows.TotalGroups);
        Assert.Equal(2, movies.TotalGroups);
        Assert.Equal("Unknown Show - S01E01.mkv", Assert.Single(unmatched.Groups).Title);
        Assert.Equal(1, unmatched.UnmatchedItems);
    }
}
