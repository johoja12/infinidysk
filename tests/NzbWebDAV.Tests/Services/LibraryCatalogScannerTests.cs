using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Services.Library;

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
            options.UseSqlite($"Data Source={_databasePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>());
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
