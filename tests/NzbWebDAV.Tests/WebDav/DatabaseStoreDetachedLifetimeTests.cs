using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Tests.WebDav;

public sealed class DatabaseStoreDetachedLifetimeTests
{
    [Theory]
    [InlineData(DavItem.ItemSubType.NzbFile)]
    [InlineData(DavItem.ItemSubType.MultipartFile)]
    public async Task DeferredSourceOpen_AfterRequestDisposed_DoesNotAccessHttpContext(
        DavItem.ItemSubType subType)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var databaseContext = new DavDatabaseContext(options);
        await databaseContext.Database.EnsureCreatedAsync();
        var database = new DavDatabaseClient(databaseContext);
        var context = new DefaultHttpContext(new FeatureCollection());
        var item = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "movie.mkv",
            Type = DavItem.ItemType.UsenetFile,
            SubType = subType,
            FileSize = 1,
        };
        var file = CreateExposedStoreFile(subType, item, context, database);

        context.Uninitialize();

        await Assert.ThrowsAsync<MissingFilePayloadException>(
            () => file.OpenSourceAsync(CancellationToken.None));
    }

    private static IExposedStoreFile CreateExposedStoreFile(
        DavItem.ItemSubType subType,
        DavItem item,
        HttpContext context,
        DavDatabaseClient database) => subType switch
    {
        DavItem.ItemSubType.NzbFile =>
            new ExposedNzbFile(item, context, database),
        DavItem.ItemSubType.MultipartFile =>
            new ExposedMultipartFile(item, context, database),
        _ => throw new ArgumentOutOfRangeException(nameof(subType), subType, null),
    };

    private interface IExposedStoreFile
    {
        Task<Stream> OpenSourceAsync(CancellationToken cancellationToken);
    }

    private sealed class ExposedNzbFile(
        DavItem item,
        HttpContext context,
        DavDatabaseClient database)
        : DatabaseStoreNzbFile(item, context, database, null!, new ConfigManager(), null!),
            IExposedStoreFile
    {
        public Task<Stream> OpenSourceAsync(CancellationToken cancellationToken) =>
            base.GetStreamAsync(cancellationToken);
    }

    private sealed class ExposedMultipartFile(
        DavItem item,
        HttpContext context,
        DavDatabaseClient database)
        : DatabaseStoreMultipartFile(
            item,
            context,
            database,
            null!,
            new ConfigManager(),
            null!,
            null!),
            IExposedStoreFile
    {
        public Task<Stream> OpenSourceAsync(CancellationToken cancellationToken) =>
            base.GetStreamAsync(cancellationToken);
    }
}
