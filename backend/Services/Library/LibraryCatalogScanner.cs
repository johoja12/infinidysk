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
