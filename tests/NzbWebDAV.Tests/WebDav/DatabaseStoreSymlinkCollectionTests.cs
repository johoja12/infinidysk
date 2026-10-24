using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NWebDav.Server;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.WebDav;

public sealed class DatabaseStoreSymlinkCollectionTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-symlink-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _client = null!;
    private ConfigManager _config = null!;
    private WebsocketManager _websocket = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>().Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _client = new DavDatabaseClient(_context);
        _config = new ConfigManager();
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "false" }]);
        _websocket = new WebsocketManager();
    }

    [Fact]
    public async Task DeleteItemAsync_FromSymlinkRoot_ReturnsForbidden()
    {
        var status = await new DatabaseStoreSymlinkCollection(DavItem.SymlinkFolder, _client, _config, _websocket)
            .DeleteItemAsync("movies", CancellationToken.None);
        Assert.Equal(DavStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task DeleteItemAsync_FileDelete_DoesNotRemoveHistory()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(release, _client, _config, _websocket)
            .DeleteItemAsync("episode.mkv.rclonelink", CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId));
    }

    [Fact]
    public async Task DeleteItemAsync_ReleaseDirectory_PrunesHistoryAndPreservesDavItems()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(category, _client, _config, _websocket)
            .DeleteItemAsync(release.Name, CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.Null(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId));
        Assert.NotNull(await _context.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == release.Id));
    }

    [Fact]
    public async Task DeleteItemAsync_WhenReadonlyWebdavEnabled_ReleaseDirectoryDeleteSucceedsAndPreservesHistory()
    {
        // *Arr imports move the symlink via copy-and-delete, so deletes under
        // /completed-symlinks must succeed even when read-only enforcement is on.
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "true" }]);
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "tv");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(category, _client, _config, _websocket)
            .DeleteItemAsync(release.Name, CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId));
    }

    [Fact]
    public async Task DeleteItemAsync_WhenReadonlyWebdavEnabled_FileDeleteReturnsNoContent()
    {
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "true" }]);
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "tv");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var historyId = Guid.NewGuid();
        _context.Items.AddRange(category, release);
        _context.HistoryItems.Add(CreateHistory(historyId, "episode.nzb", category.Name, release.Id));
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(release, _client, _config, _websocket)
            .DeleteItemAsync("episode.mkv.rclonelink", CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId));
    }

    [Fact]
    public async Task DeleteItemAsync_ReleaseDirectoryWithoutHistory_ReturnsNoContentAndRemovesNothing()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        _context.Items.AddRange(category, release);
        await _context.SaveChangesAsync();
        var status = await new DatabaseStoreSymlinkCollection(category, _client, _config, _websocket)
            .DeleteItemAsync(release.Name, CancellationToken.None);
        Assert.Equal(DavStatusCode.NoContent, status);
        Assert.NotNull(await _context.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == release.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileRemoval_PersistsAndPreservesHistoryMediaAndUnimportedSibling(bool readOnly)
    {
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = readOnly.ToString().ToLowerInvariant() }]);
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "pack");
        var file = NewFile(release, "imported.mp4");
        var sibling = NewFile(release, "unimported.mp4");
        var history = CreateHistory(Guid.NewGuid(), "pack.nzb", category.Name, release.Id);
        _context.Items.AddRange(category, release, file, sibling);
        _context.HistoryItems.Add(history);
        await _context.SaveChangesAsync();
        var state = _databasePath + ".deletions";
        DatabaseStoreSymlinkCollection Collection() => new(release, _client, _config, _websocket, state);
        Assert.NotNull(await Collection().GetItemAsync("imported.mp4.rclonelink", CancellationToken.None));
        Assert.Equal(DavStatusCode.NoContent, await Collection().DeleteItemAsync("imported.mp4.rclonelink", CancellationToken.None));
        Assert.True(new CompletedSymlinkDeletionStore(state).IsDeleted(file.Id));
        Assert.Null(await Collection().GetItemAsync("imported.mp4.rclonelink", CancellationToken.None));
        Assert.NotNull(await Collection().GetItemAsync("unimported.mp4.rclonelink", CancellationToken.None));
        var names = new List<string>();
        await foreach (var item in Collection().GetItemsAsync(CancellationToken.None)) names.Add(item.Name);
        Assert.DoesNotContain("imported.mp4.rclonelink", names);
        Assert.Contains("unimported.mp4.rclonelink", names);
        Assert.NotNull(await _context.Items.AsNoTracking().SingleAsync(x => x.Id == file.Id));
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == history.Id));
        Assert.Equal(DavStatusCode.NoContent, await Collection().DeleteItemAsync("imported.mp4.rclonelink", CancellationToken.None));
        file.Name = "old-name.mp4";
        file.Path = Path.Join(release.Path, file.Name);
        _context.Items.Add(NewFile(release, "imported.mp4"));
        await _context.SaveChangesAsync();
        Assert.NotNull(await Collection().GetItemAsync("imported.mp4.rclonelink", CancellationToken.None));
        Assert.Null(await Collection().GetItemAsync("old-name.mp4.rclonelink", CancellationToken.None));
    }

    [Fact]
    public async Task FileRemoval_DoesNotClaimSuccessWhenPersistenceFails()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var release = NewDir(Guid.NewGuid(), category, "pack");
        var file = NewFile(release, "scene.mp4");
        _context.Items.AddRange(category, release, file);
        await _context.SaveChangesAsync();
        var collection = new DatabaseStoreSymlinkCollection(release, _client, _config, _websocket, _databasePath);
        Assert.Equal(DavStatusCode.InternalServerError, await collection.DeleteItemAsync("scene.mp4.rclonelink", CancellationToken.None));
        Assert.NotNull(await collection.GetItemAsync("scene.mp4.rclonelink", CancellationToken.None));
        Assert.NotNull(await _context.Items.AsNoTracking().SingleAsync(x => x.Id == file.Id));
    }

    private static DavItem NewFile(DavItem parent, string name) =>
        DavItem.New(Guid.NewGuid(), parent, name, 100, DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile, null, null, null, null);

    private static DavItem NewDir(Guid id, DavItem parent, string name) =>
        DavItem.New(id, parent, name, null, DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, null, null);

    private static HistoryItem CreateHistory(Guid id, string fileName, string category, Guid downloadDirId) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = fileName,
        JobName = Path.GetFileNameWithoutExtension(fileName),
        Category = category,
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        DownloadDirId = downloadDirId,
        TotalSegmentBytes = 100,
        DownloadTimeSeconds = 1,
    };

    public async Task DisposeAsync() { await _context.DisposeAsync(); if (Directory.Exists(_databasePath + ".deletions")) Directory.Delete(_databasePath + ".deletions", true); try { File.Delete(_databasePath); } catch (IOException) { } }
}
