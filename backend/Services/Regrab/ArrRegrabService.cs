using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services.Regrab;

/// <summary>
/// The one place InfiniDysk asks Sonarr/Radarr for another copy of a file. Used by the
/// file-modal Regrab action, by failed migration imports, and (for state only) by health
/// repair. A regrab resolves the owning Arr media file from the library link path, removes
/// only the library symlink (journaled, guarded by <see cref="LibrarySymlinkGuard"/>), then
/// removes the orphaned Arr file record — blocklisting the release when its download id is
/// known — and requests a replacement search through <see cref="ArrReplacementSearchBudget"/>.
/// Requests are persisted per item (<see cref="ArrRegrabRequest.DedupKey"/>), retried with
/// backoff when Arr is slow or unreachable, and serialized so Arr sees one regrab at a time.
/// </summary>
public sealed class ArrRegrabService : IDisposable
{
    public const string SourceManual = "manual";
    public const string SourceMigration = "migration";
    public const string SourceHealthRepair = "health-repair";

    internal const int MaxAttempts = 6;
    internal static readonly TimeSpan InlineBudget = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RootFolderCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PreviewTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    private readonly ConfigManager _config;
    private readonly ArrReplacementSearchBudget _budget;
    private readonly ArrInstanceBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<ArrRootFolder> Roots)> _rootFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Fingerprint, DateTimeOffset ExpiresAt)> _previewTokens =
        new(StringComparer.Ordinal);

    public ArrRegrabService(
        ConfigManager config,
        ArrReplacementSearchBudget budget,
        ArrInstanceBackoff backoff,
        TimeProvider? timeProvider = null,
        LibraryLinkRemovalJournal? journal = null)
    {
        _config = config;
        _budget = budget;
        _backoff = backoff;
        _time = timeProvider ?? TimeProvider.System;
        Journal = journal ?? new LibraryLinkRemovalJournal();
    }

    public LibraryLinkRemovalJournal Journal { get; }

    public void Dispose() => _gate.Dispose();

    /// <summary>Test seam for the database; production uses the configured provider.</summary>
    internal Func<DavDatabaseContext>? ContextFactory { get; set; }

    /// <summary>Test seam for Arr instances; production builds them from <c>arr.instances</c>.</summary>
    internal Func<IReadOnlyList<ArrClient>>? ArrClientsOverride { get; set; }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    private DavDatabaseContext CreateContext() => DavDatabaseContexts.Create(ContextFactory);

    private IReadOnlyList<ArrClient> ArrClients() =>
        ArrClientsOverride?.Invoke() ?? _config.GetArrConfig().GetArrClients().ToArray();

    // ---------------------------------------------------------------- subjects

    internal sealed record RegrabSubject(
        Guid? DavItemId,
        string ReleaseName,
        IReadOnlyList<string> LinkPaths,
        string DedupKey);

    internal async Task<(RegrabSubject? Subject, string? Error)> ResolveSubjectAsync(
        DavDatabaseContext ctx,
        Guid? davItemId,
        string? linkPath,
        CancellationToken ct)
    {
        var libraryDir = _config.GetLibraryDir();
        if (davItemId is { } id)
        {
            var item = await ctx.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (item is null)
                return (null, "File not found.");
            var links = await ctx.LinkMaps.AsNoTracking()
                .Where(x => x.DavItemId == id)
                .Select(x => x.LinkPath)
                .ToListAsync(ct).ConfigureAwait(false);
            var releaseName = item.Name;
            if (item.HistoryItemId is { } historyId)
            {
                var jobName = await ctx.HistoryItems.AsNoTracking()
                    .Where(x => x.Id == historyId)
                    .Select(x => x.JobName)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(jobName))
                    releaseName = jobName;
            }

            return (new RegrabSubject(
                id,
                releaseName,
                links.Select(link => AbsoluteLinkPath(link, libraryDir)).OfType<string>().Distinct().ToArray(),
                $"dav:{id:D}"), null);
        }

        if (string.IsNullOrWhiteSpace(linkPath))
            return (null, "A davItemId or linkPath is required.");
        var candidates = new List<string> { linkPath };
        if (libraryDir is not null && Path.IsPathRooted(linkPath)
            && HealthCheckService.IsPathWithinRoot(linkPath, libraryDir))
            candidates.Add(Path.GetRelativePath(libraryDir, linkPath));
        var row = await ctx.LinkMaps.AsNoTracking()
            .Where(x => candidates.Contains(x.LinkPath))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
            return (null, "This library link is not in the Media Library catalog.");
        var absolute = AbsoluteLinkPath(row.LinkPath, libraryDir);
        if (absolute is null)
            return (null, "Configure the Library Directory so the link path can be resolved.");
        if (row.DavItemId is { } linkedItem)
            return await ResolveSubjectAsync(ctx, linkedItem, null, ct).ConfigureAwait(false);
        return (new RegrabSubject(null, Path.GetFileName(absolute), [absolute], $"link:{absolute}"), null);
    }

    internal static string? AbsoluteLinkPath(string linkPath, string? libraryDir)
    {
        if (Path.IsPathRooted(linkPath))
            return Path.GetFullPath(linkPath);
        return libraryDir is null ? null : Path.GetFullPath(Path.Join(libraryDir, linkPath));
    }

    // ---------------------------------------------------------------- Arr resolution

    internal sealed record ArrResolution(
        ArrRegrabTarget? Target,
        ArrClient? Client,
        string? Problem,
        bool Retryable);

    internal async Task<ArrResolution> ResolveArrAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var clients = ArrClients();
        if (clients.Count == 0)
            return new(null, null, "No Sonarr or Radarr instance is enabled.", false);
        if (paths.Count == 0)
            return new(null, null, "This file has no library link, so its Sonarr/Radarr item cannot be identified.", false);

        var matches = new List<(ArrClient Client, ArrMediaFileMatch Match, string Path)>();
        string? unreachable = null;
        var matchedRoot = false;
        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();
            if (_backoff.IsInBackoff(client.Host))
            {
                unreachable = $"{AppName(client)} at {ArrRegrabTarget.DisplayHost(client.Host)} is not responding; " +
                              $"InfiniDysk is backing off for {_backoff.GetRemainingBackoff(client.Host).TotalSeconds:0} s.";
                continue;
            }

            List<ArrRootFolder> roots;
            try
            {
                roots = await GetRootFoldersAsync(client, ct).ConfigureAwait(false);
                _backoff.RecordSuccess(client.Host);
            }
            catch (Exception e) when (IsArrFailure(e, ct))
            {
                _backoff.RecordFailure(client.Host, e);
                unreachable = DescribeArrFailure(client, e);
                continue;
            }

            foreach (var path in paths)
            {
                if (!roots.Any(root => !string.IsNullOrEmpty(root.Path)
                                       && HealthCheckService.IsPathWithinRoot(path, root.Path!)))
                    continue;
                matchedRoot = true;
                try
                {
                    var match = await client.FindMediaFileAsync(path, ct).ConfigureAwait(false);
                    if (match is not null)
                        matches.Add((client, match, path));
                }
                catch (Exception e) when (IsArrFailure(e, ct))
                {
                    _backoff.RecordFailure(client.Host, e);
                    unreachable = DescribeArrFailure(client, e);
                }
            }
        }

        var distinct = matches
            .GroupBy(m => (Host: m.Client.Host.TrimEnd('/').ToLowerInvariant(), m.Match.Kind, m.Match.FileId))
            .Select(group => group.First())
            .ToArray();
        if (distinct.Length > 1)
            return new(null, null, "More than one Sonarr/Radarr media file matches this item; regrab it in Sonarr/Radarr directly.", false);
        if (distinct.Length == 1)
        {
            var (client, match, path) = distinct[0];
            if (match.MediaIds.Count == 0)
                return new(null, null, $"{AppName(client)} has no episode or movie linked to this file.", false);
            return new(new ArrRegrabTarget(
                AppName(client),
                client.Host,
                match.Kind == ArrMediaKind.Movie ? "movie" : "episode",
                match.FileId,
                match.MediaIds,
                path), client, null, false);
        }

        if (unreachable is not null)
            return new(null, null, unreachable, true);
        return matchedRoot
            ? new(null, null, "Sonarr/Radarr does not list a media file at this library path.", false)
            : new(null, null, "No Sonarr/Radarr root folder contains this library path.", false);
    }

    private async Task<List<ArrRootFolder>> GetRootFoldersAsync(ArrClient client, CancellationToken ct)
    {
        var key = client.Host.TrimEnd('/');
        if (_rootFolders.TryGetValue(key, out var cached) && _time.GetUtcNow() - cached.At < RootFolderCacheLifetime)
            return cached.Roots;
        var roots = await client.GetRootFolders(ct).ConfigureAwait(false);
        _rootFolders[key] = (_time.GetUtcNow(), roots);
        return roots;
    }

    private static string AppName(ArrClient client) => client switch
    {
        SonarrClient => "Sonarr",
        RadarrClient => "Radarr",
        _ => "Sonarr/Radarr",
    };

    private static string AppName(string? mediaKind) => mediaKind == "movie" ? "Radarr" : "Sonarr";

    private static bool IsArrFailure(Exception e, CancellationToken ct) =>
        !(e is OperationCanceledException && ct.IsCancellationRequested)
        && e is HttpRequestException or TaskCanceledException or OperationCanceledException
            or InvalidOperationException or InvalidDataException or JsonException or IOException;

    internal static string DescribeArrFailure(ArrClient client, Exception e) =>
        DescribeArrFailure(AppName(client), client.Host, e);

    internal static string DescribeArrFailure(string app, string host, Exception e)
    {
        var display = ArrRegrabTarget.DisplayHost(host);
        return e switch
        {
            ArrRequestTimeoutException timeout =>
                $"{app} at {display} did not respond within {timeout.Budget.TotalSeconds:0} seconds. " +
                "It may be busy; the regrab will be retried automatically.",
            TaskCanceledException or OperationCanceledException =>
                $"{app} at {display} did not respond in time. It may be busy; the regrab will be retried automatically.",
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                $"{app} at {display} rejected the API key.",
            HttpRequestException { StatusCode: { } status } =>
                $"{app} at {display} returned HTTP {(int)status}.",
            HttpRequestException =>
                $"{app} at {display} could not be reached.",
            JsonException or InvalidDataException =>
                $"{app} at {display} returned an unusable response.",
            _ => $"{app} at {display}: {e.Message}",
        };
    }

    private static bool IsRetryable(Exception e) => e switch
    {
        TaskCanceledException or OperationCanceledException or IOException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status == HttpStatusCode.TooManyRequests,
        _ => false,
    };

    // ---------------------------------------------------------------- preview / request

    public async Task<ArrRegrabPreview?> PreviewAsync(Guid? davItemId, string? linkPath, CancellationToken ct)
    {
        await using var ctx = CreateContext();
        var (subject, error) = await ResolveSubjectAsync(ctx, davItemId, linkPath, ct).ConfigureAwait(false);
        if (subject is null)
            return error == "File not found." || error?.StartsWith("This library link", StringComparison.Ordinal) == true
                ? null
                : new ArrRegrabPreview(false, error, davItemId?.ToString() ?? linkPath ?? "", null, false, null, null);
        var existing = await FindActiveAsync(ctx, subject, ct).ConfigureAwait(false);
        var primaryPath = subject.LinkPaths.Count > 0 ? subject.LinkPaths[0] : null;
        var oldLibrary = primaryPath is not null && IsOldLibraryLink(primaryPath);
        if (existing is not null)
        {
            return new ArrRegrabPreview(
                false,
                "A regrab is already requested for this file.",
                subject.ReleaseName,
                existing.LibraryPath ?? primaryPath,
                oldLibrary,
                null,
                ArrRegrabRequestView.From(existing));
        }

        var linkProblem = DescribeLinkProblem(subject);
        if (linkProblem is not null)
            return new ArrRegrabPreview(false, linkProblem, subject.ReleaseName, primaryPath, oldLibrary, null,
                await LatestViewAsync(ctx, subject, ct).ConfigureAwait(false));

        var resolution = await ResolveArrAsync(subject.LinkPaths, ct).ConfigureAwait(false);
        return new ArrRegrabPreview(
            resolution.Target is not null,
            resolution.Problem,
            subject.ReleaseName,
            resolution.Target?.LibraryPath ?? primaryPath,
            oldLibrary,
            resolution.Target,
            await LatestViewAsync(ctx, subject, ct).ConfigureAwait(false));
    }

    private string? DescribeLinkProblem(RegrabSubject subject)
    {
        if (subject.LinkPaths.Count == 0)
            return "This file has no library link, so its Sonarr/Radarr item cannot be identified.";
        var roots = LibrarySymlinkGuard.ConfiguredRoots(_config);
        var checks = subject.LinkPaths.Select(path => LibrarySymlinkGuard.Inspect(path, roots)).ToArray();
        if (checks.Any(check => check.Kind is LibraryLinkInspection.Symlink or LibraryLinkInspection.Missing))
            return null;
        return checks[0].Message;
    }

    private bool IsOldLibraryLink(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            if (target is null)
                return false;
            var mount = _config.GetRcloneMountDir();
            return string.IsNullOrWhiteSpace(mount)
                   || !HealthCheckService.IsPathWithinRoot(target, mount);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<ArrRegrabRequest?> FindActiveAsync(
        DavDatabaseContext ctx,
        RegrabSubject subject,
        CancellationToken ct)
    {
        var rows = await ctx.ArrRegrabRequests.AsNoTracking()
            .Where(x => x.DedupKey == subject.DedupKey
                        || (x.LibraryPath != null && subject.LinkPaths.Contains(x.LibraryPath)))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(x => ArrRegrabStatus.IsActive(x.Status))
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefault();
    }

    private static async Task<ArrRegrabRequestView?> LatestViewAsync(
        DavDatabaseContext ctx,
        RegrabSubject subject,
        CancellationToken ct)
    {
        var row = await ctx.ArrRegrabRequests.AsNoTracking()
            .Where(x => x.DedupKey == subject.DedupKey)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : ArrRegrabRequestView.From(row);
    }

    /// <summary>
    /// Handles the file-modal Regrab action. Idempotent: an active request for the same
    /// item is returned unchanged. Waits up to <see cref="InlineBudget"/> for the Arr calls;
    /// slower instances keep being retried by <see cref="ArrRegrabWorker"/>.
    /// </summary>
    public async Task<ArrRegrabResult?> RequestAsync(Guid? davItemId, string? linkPath, CancellationToken ct)
    {
        Guid requestId;
        await using (var ctx = CreateContext())
        {
            var (subject, error) = await ResolveSubjectAsync(ctx, davItemId, linkPath, ct).ConfigureAwait(false);
            if (subject is null)
                return error == "File not found." || error?.StartsWith("This library link", StringComparison.Ordinal) == true
                    ? null
                    : new ArrRegrabResult(false, error ?? "This file cannot be regrabbed.", null);

            var existing = await FindActiveAsync(ctx, subject, ct).ConfigureAwait(false);
            if (existing is not null)
                return new ArrRegrabResult(true, "A regrab is already requested for this file.", ArrRegrabRequestView.From(existing));

            var linkProblem = DescribeLinkProblem(subject);
            if (linkProblem is not null)
                return new ArrRegrabResult(false, linkProblem, null);

            var resolution = await ResolveArrAsync(subject.LinkPaths, ct).ConfigureAwait(false);
            if (resolution.Target is null && !resolution.Retryable)
                return new ArrRegrabResult(false, resolution.Problem ?? "No Sonarr/Radarr mapping was found.", null);

            var row = await ctx.ArrRegrabRequests
                .FirstOrDefaultAsync(x => x.DedupKey == subject.DedupKey, ct).ConfigureAwait(false);
            var now = UtcNow;
            if (row is null)
            {
                row = new ArrRegrabRequest { Id = Guid.NewGuid(), DedupKey = subject.DedupKey, CreatedAt = now };
                ctx.ArrRegrabRequests.Add(row);
            }

            ResetForNewAttempt(row, SourceManual, subject.ReleaseName, "requested from the file details", now);
            row.DavItemId = subject.DavItemId;
            row.LibraryPath = resolution.Target?.LibraryPath ?? subject.LinkPaths[0];
            ApplyTarget(row, resolution.Target);
            if (resolution.Target is null)
            {
                row.LastError = resolution.Problem;
                row.NextAttemptAt = now + RetryDelay(1);
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            requestId = row.Id;
            if (resolution.Target is null)
            {
                Log.Warning("Regrab for {Release} queued for retry. Reason: {Reason}", subject.ReleaseName, resolution.Problem);
                return new ArrRegrabResult(true, resolution.Problem!, ArrRegrabRequestView.From(row));
            }
        }

        // The Arr calls must not be tied to the HTTP request: a browser that gives up must
        // not cancel a delete Sonarr may already have applied.
        var processing = ProcessAsync(requestId, CancellationToken.None);
        var finished = await Task.WhenAny(processing, Task.Delay(InlineBudget, ct)).ConfigureAwait(false) == processing;
        await using var read = CreateContext();
        var current = await read.ArrRegrabRequests.AsNoTracking()
            .FirstAsync(x => x.Id == requestId, CancellationToken.None).ConfigureAwait(false);
        var view = ArrRegrabRequestView.From(current);
        if (!finished)
            return new ArrRegrabResult(true,
                $"{AppName(current.ArrMediaKind)} is responding slowly; the regrab continues in the background.", view);
        return current.Status switch
        {
            ArrRegrabStatus.Requested => new ArrRegrabResult(true,
                $"Regrab requested: {AppName(current.ArrMediaKind)} removed the file and is searching for a replacement.", view),
            ArrRegrabStatus.SearchWithheld => new ArrRegrabResult(true,
                $"{AppName(current.ArrMediaKind)} removed the file, but the replacement search was withheld by the per-item search limit.", view),
            ArrRegrabStatus.Pending => new ArrRegrabResult(true,
                current.LastError ?? "The regrab will be retried automatically.", view),
            _ => new ArrRegrabResult(false, current.LastError ?? "The regrab could not be completed.", view),
        };
    }

    private static void ResetForNewAttempt(ArrRegrabRequest row, string source, string releaseName, string? reason, DateTime now)
    {
        row.Source = source;
        row.Status = ArrRegrabStatus.Pending;
        row.ReleaseName = releaseName;
        row.Reason = reason;
        row.LinkRemoved = false;
        row.PreviousLinkTarget = null;
        row.ArrFileRemoved = false;
        row.Blocklisted = false;
        row.Attempts = 0;
        row.NextAttemptAt = null;
        row.LastError = null;
        row.RequestedAt = null;
        row.CompletedAt = null;
        row.UpdatedAt = now;
    }

    private static void ApplyTarget(ArrRegrabRequest row, ArrRegrabTarget? target)
    {
        if (target is null)
            return;
        row.ArrHost = target.Host;
        row.ArrMediaKind = target.MediaKind;
        row.ArrFileId = target.FileId;
        row.ArrMediaIds = string.Join(",", target.MediaIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        row.LibraryPath = target.LibraryPath;
    }

    private static ArrMediaFileMatch? MatchFrom(ArrRegrabRequest row)
    {
        if (row.ArrFileId is not { } fileId || row.ArrMediaKind is null || string.IsNullOrEmpty(row.ArrMediaIds))
            return null;
        var ids = row.ArrMediaIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => int.Parse(id, CultureInfo.InvariantCulture))
            .ToArray();
        return new ArrMediaFileMatch(row.ArrMediaKind == "movie" ? ArrMediaKind.Movie : ArrMediaKind.Episode, fileId, ids);
    }

    // ---------------------------------------------------------------- processing

    /// <summary>Processes one pending request. Serialized across the process.</summary>
    public async Task ProcessAsync(Guid requestId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProcessCoreAsync(requestId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Processes the oldest due pending request, if any. Returns false when none is due.</summary>
    public async Task<bool> ProcessNextDueAsync(CancellationToken ct)
    {
        Guid? next;
        await using (var ctx = CreateContext())
        {
            var now = UtcNow;
            next = await ctx.ArrRegrabRequests.AsNoTracking()
                .Where(x => x.Status == ArrRegrabStatus.Pending && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                .OrderBy(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        if (next is null)
            return false;
        await ProcessAsync(next.Value, ct).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessCoreAsync(Guid requestId, CancellationToken ct)
    {
        await using var ctx = CreateContext();
        var row = await ctx.ArrRegrabRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct).ConfigureAwait(false);
        if (row is null || row.Status != ArrRegrabStatus.Pending)
            return;

        row.Attempts++;
        row.UpdatedAt = UtcNow;
        string? app = row.ArrMediaKind is null ? null : AppName(row.ArrMediaKind);
        try
        {
            // 1. Resolve the owning Arr media file before touching anything.
            var match = MatchFrom(row);
            ArrClient? client;
            if (match is not null)
            {
                client = ArrClients().FirstOrDefault(c =>
                    string.Equals(c.Host.TrimEnd('/'), row.ArrHost?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                if (client is null)
                {
                    await FinishAsync(ctx, row, ArrRegrabStatus.Failed,
                        "The Sonarr/Radarr instance that owned this file is no longer enabled.", ct).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                if (row.LibraryPath is null)
                {
                    await FinishAsync(ctx, row, ArrRegrabStatus.Skipped, "The item has no library link path.", ct)
                        .ConfigureAwait(false);
                    return;
                }

                var resolution = await ResolveArrAsync([row.LibraryPath], ct).ConfigureAwait(false);
                if (resolution.Target is null)
                {
                    if (resolution.Retryable)
                        await DeferAsync(ctx, row, resolution.Problem!, null, ct).ConfigureAwait(false);
                    else
                        await FinishAsync(ctx, row, ArrRegrabStatus.Skipped, resolution.Problem!, ct).ConfigureAwait(false);
                    return;
                }

                ApplyTarget(row, resolution.Target);
                client = resolution.Client!;
                match = resolution.Target.ToMatch();
                app = resolution.Target.App;
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            if (_backoff.IsInBackoff(client.Host))
            {
                await DeferAsync(ctx, row,
                    $"{AppName(client)} at {ArrRegrabTarget.DisplayHost(client.Host)} is not responding; waiting before retrying.",
                    _backoff.GetRemainingBackoff(client.Host), ct).ConfigureAwait(false);
                return;
            }

            // 2. Remove the library symlink (journaled) so Arr sees the file as missing.
            if (!row.LinkRemoved && row.LibraryPath is not null)
            {
                var stop = await RemoveLibraryLinkAsync(ctx, row, ct).ConfigureAwait(false);
                if (stop)
                    return;
            }

            // 3. Remove the orphaned Arr file record and request a replacement search.
            if (!row.ArrFileRemoved)
            {
                if (row.Attempts > 1)
                {
                    // A previous attempt may have reached Arr before failing; never delete twice.
                    var current = await client.GetCurrentMediaFileIdAsync(match, ct).ConfigureAwait(false);
                    _backoff.RecordSuccess(client.Host);
                    if (current is null)
                    {
                        row.ArrFileRemoved = true;
                    }
                    else if (current != match.FileId)
                    {
                        await FinishAsync(ctx, row, ArrRegrabStatus.Replaced,
                            $"{AppName(client)} already has a different file for this item.", ct).ConfigureAwait(false);
                        return;
                    }
                }
            }

            if (!row.ArrFileRemoved)
            {
                await RemoveArrFileAndSearchAsync(ctx, row, client, match, ct).ConfigureAwait(false);
                return;
            }

            await RequestSearchOnlyAsync(ctx, row, client, match, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            row.Attempts--;
            await SaveQuietlyAsync(ctx).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var host = row.ArrHost ?? "Sonarr/Radarr";
            if (row.ArrHost is not null)
                _backoff.RecordFailure(row.ArrHost, e);
            var message = row.ArrHost is null
                ? e.Message
                : DescribeArrFailure(app ?? "Sonarr/Radarr", host, e);
            if (IsRetryable(e) && row.Attempts < MaxAttempts)
            {
                await DeferAsync(ctx, row, message, null, CancellationToken.None).ConfigureAwait(false);
                Log.Debug(e, "Regrab transient Arr failure stack");
                return;
            }

            if (e.TryGetKnownErrorMessage(out _) || e is HttpRequestException or InvalidOperationException
                    or TaskCanceledException or IOException or JsonException or InvalidDataException)
            {
                Log.Debug(e, "Regrab known failure stack");
            }
            else
            {
                Log.Error(e, "Unexpected error while regrabbing {Release}", row.ReleaseName);
            }

            await FinishAsync(ctx, row, ArrRegrabStatus.Failed, message, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Returns true when processing must stop (the row was finished or deferred).</summary>
    private async Task<bool> RemoveLibraryLinkAsync(DavDatabaseContext ctx, ArrRegrabRequest row, CancellationToken ct)
    {
        var roots = LibrarySymlinkGuard.ConfiguredRoots(_config);
        var path = row.LibraryPath!;
        Guid? legacyId = row.Source == SourceMigration ? LegacyIdFrom(row.DedupKey) : null;
        var check = LibrarySymlinkGuard.Inspect(path, roots, row.ExpectedLinkTarget, legacyId);
        switch (check.Kind)
        {
            case LibraryLinkInspection.Symlink:
                break;
            case LibraryLinkInspection.Missing when Journal.HasRemovalFor(row.Id, path):
                // An earlier attempt removed it but stopped before recording that.
                row.LinkRemoved = true;
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return false;
            case LibraryLinkInspection.Missing when row.Source != SourceMigration:
                // Nothing to remove; the Arr record is already an orphan.
                return false;
            case LibraryLinkInspection.Missing:
            case LibraryLinkInspection.TargetMismatch:
                await FinishAsync(ctx, row, ArrRegrabStatus.Skipped,
                    check.Message + " Nothing was changed in Sonarr/Radarr.", ct).ConfigureAwait(false);
                return true;
            default:
                await FinishAsync(ctx, row, ArrRegrabStatus.Failed,
                    check.Message + " Nothing was changed in Sonarr/Radarr.", ct).ConfigureAwait(false);
                return true;
        }

        var arrTarget = MatchFrom(row) is { } match
            ? $"{AppName(row.ArrMediaKind)} {row.ArrMediaKind} {string.Join(",", match.MediaIds)} (file {match.FileId}) on {ArrRegrabTarget.DisplayHost(row.ArrHost!)}"
            : null;
        LibraryLinkRemovalJournal.Entry Entry(string evt, string? error = null) => new()
        {
            Timestamp = _time.GetUtcNow(),
            Event = evt,
            RequestId = row.Id,
            LinkPath = path,
            PreviousTarget = check.Target,
            DavItemId = row.DavItemId,
            ReleaseName = row.ReleaseName,
            ArrTarget = arrTarget,
            Source = row.Source,
            Reason = row.Reason,
            Error = error,
        };

        Journal.Append(Entry("intent"));
        try
        {
            LibrarySymlinkGuard.RemoveSymlink(path, roots, row.ExpectedLinkTarget, legacyId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Journal.Append(Entry("failed", e.Message));
            var message = e is UnauthorizedAccessException || (e is IOException && e.Message.Contains("Read-only", StringComparison.OrdinalIgnoreCase))
                ? $"InfiniDysk cannot remove the library link (the library directory is read-only here): {path}."
                : $"Could not remove the library link {path}: {e.Message}";
            await FinishAsync(ctx, row, ArrRegrabStatus.Failed, message + " Nothing was changed in Sonarr/Radarr.", ct)
                .ConfigureAwait(false);
            Log.Debug(e, "Regrab library-link removal failure stack");
            return true;
        }

        Journal.Append(Entry("removed"));
        row.LinkRemoved = true;
        row.PreviousLinkTarget = check.Target;
        row.UpdatedAt = UtcNow;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return false;
    }

    private async Task RemoveArrFileAndSearchAsync(
        DavDatabaseContext ctx,
        ArrRegrabRequest row,
        ArrClient client,
        ArrMediaFileMatch match,
        CancellationToken ct)
    {
        var downloadId = await ResolveDownloadIdAsync(ctx, row, client, match, ct).ConfigureAwait(false);
        if (downloadId is { } id)
        {
            var outcome = await client.RemoveAndBlocklist(match, id, keys => Reserve(client, keys), ct)
                .ConfigureAwait(false);
            _backoff.RecordSuccess(client.Host);
            switch (outcome)
            {
                case ArrRepairOutcome.RemoveAndBlocklistSucceeded:
                    row.ArrFileRemoved = true;
                    row.Blocklisted = true;
                    await FinishAsync(ctx, row, ArrRegrabStatus.Requested, null, ct).ConfigureAwait(false);
                    return;
                case ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld:
                    row.ArrFileRemoved = true;
                    row.Blocklisted = true;
                    await FinishAsync(ctx, row, ArrRegrabStatus.SearchWithheld,
                        "The replacement search was withheld by the per-item search limit.", ct).ConfigureAwait(false);
                    return;
                case ArrRepairOutcome.MediaRemovedBlocklistConfirmedSearchUnconfirmed:
                    row.ArrFileRemoved = true;
                    row.Blocklisted = true;
                    await FinishAsync(ctx, row, ArrRegrabStatus.Requested,
                        "The release was blocklisted; the replacement search could not be confirmed.", ct).ConfigureAwait(false);
                    return;
                case ArrRepairOutcome.MediaRemovedBlocklistUnconfirmed:
                    row.ArrFileRemoved = true;
                    await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    await RequestSearchOnlyAsync(ctx, row, client, match, ct).ConfigureAwait(false);
                    return;
                case ArrRepairOutcome.DownloadHistoryNotFound:
                    break; // fall back to removing the record without a blocklist entry
                default:
                    await FinishAsync(ctx, row, ArrRegrabStatus.Skipped,
                        $"{AppName(client)} no longer lists this media file.", ct).ConfigureAwait(false);
                    return;
            }
        }

        var cleanup = await client.RemoveMissingPayloadAndSearchAsync(match, keys => Reserve(client, keys), ct)
            .ConfigureAwait(false);
        _backoff.RecordSuccess(client.Host);
        switch (cleanup)
        {
            case ArrMissingPayloadCleanupOutcome.RemovedSearchRequested:
                row.ArrFileRemoved = true;
                await FinishAsync(ctx, row, ArrRegrabStatus.Requested, null, ct).ConfigureAwait(false);
                return;
            case ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld:
                row.ArrFileRemoved = true;
                await FinishAsync(ctx, row, ArrRegrabStatus.SearchWithheld,
                    "The replacement search was withheld by the per-item search limit.", ct).ConfigureAwait(false);
                return;
            case ArrMissingPayloadCleanupOutcome.RemovedSearchFailed:
                row.ArrFileRemoved = true;
                await DeferAsync(ctx, row,
                    $"{AppName(client)} removed the file but did not accept the search; retrying the search.", null, ct)
                    .ConfigureAwait(false);
                return;
            case ArrMissingPayloadCleanupOutcome.RemovedNoSearchTargets:
                row.ArrFileRemoved = true;
                await FinishAsync(ctx, row, ArrRegrabStatus.Failed,
                    $"{AppName(client)} removed the file but has no episode or movie to search for.", ct).ConfigureAwait(false);
                return;
            default:
                await FinishAsync(ctx, row, ArrRegrabStatus.Skipped,
                    $"{AppName(client)} no longer lists this media file.", ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task RequestSearchOnlyAsync(
        DavDatabaseContext ctx,
        ArrRegrabRequest row,
        ArrClient client,
        ArrMediaFileMatch match,
        CancellationToken ct)
    {
        if (!Reserve(client, match.MediaKeys))
        {
            await FinishAsync(ctx, row, ArrRegrabStatus.SearchWithheld,
                "The replacement search was withheld by the per-item search limit.", ct).ConfigureAwait(false);
            return;
        }

        await client.RequestSearchAsync(match, ct).ConfigureAwait(false);
        _backoff.RecordSuccess(client.Host);
        await FinishAsync(ctx, row, ArrRegrabStatus.Requested, null, ct).ConfigureAwait(false);
    }

    private async Task<Guid?> ResolveDownloadIdAsync(
        DavDatabaseContext ctx,
        ArrRegrabRequest row,
        ArrClient client,
        ArrMediaFileMatch match,
        CancellationToken ct)
    {
        if (row.DavItemId is { } davItemId)
        {
            var known = await ctx.Items.AsNoTracking()
                .Where(x => x.Id == davItemId)
                .Select(x => x.ArrDownloadId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (known is not null)
                return known;
        }

        if (row.LibraryPath is null)
            return null;
        try
        {
            var history = await client.CollectMediaImportHistoryAsync(match, ct).ConfigureAwait(false);
            var resolution = ArrDownloadIdResolver.Resolve(history.Records, match, row.LibraryPath);
            return resolution.Kind == ArrDownloadIdResolutionKind.Unique && history.Exhausted
                ? resolution.DownloadId
                : null;
        }
        catch (Exception e) when (IsArrFailure(e, ct))
        {
            // Without import history the release cannot be blocklisted, but the regrab
            // itself can still proceed; the history lookup is best-effort.
            Log.Debug(e, "Regrab could not read Arr import history for {Release}", row.ReleaseName);
            return null;
        }
    }

    private bool Reserve(ArrClient client, IReadOnlyList<string> mediaKeys)
    {
        var arrConfig = _config.GetArrConfig();
        return _budget.TryReserveAll(
            mediaKeys.Select(key => $"{client.Host.TrimEnd('/').ToLowerInvariant()}|{key}").ToArray(),
            arrConfig.EffectiveQueueReplacementSearchLimit(),
            arrConfig.EffectiveQueueReplacementSearchWindow());
    }

    private async Task DeferAsync(
        DavDatabaseContext ctx,
        ArrRegrabRequest row,
        string reason,
        TimeSpan? delay,
        CancellationToken ct)
    {
        if (row.Attempts >= MaxAttempts)
        {
            await FinishAsync(ctx, row, ArrRegrabStatus.Failed,
                $"{reason} Gave up after {row.Attempts} attempts; use Regrab again to retry.", ct).ConfigureAwait(false);
            return;
        }

        var wait = delay is { } explicitDelay && explicitDelay > TimeSpan.Zero ? explicitDelay : RetryDelay(row.Attempts);
        row.LastError = reason;
        row.NextAttemptAt = UtcNow + wait;
        row.UpdatedAt = UtcNow;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Warning("Regrab for {Release} deferred; retrying in {Delay}. Reason: {Reason}",
            row.ReleaseName, FormatDelay(wait), reason);
    }

    internal static TimeSpan RetryDelay(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));
        var delay = TimeSpan.FromMinutes(minutes);
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} min" : $"{delay.TotalSeconds:0} s";

    private async Task FinishAsync(
        DavDatabaseContext ctx,
        ArrRegrabRequest row,
        string status,
        string? message,
        CancellationToken ct)
    {
        var now = UtcNow;
        row.Status = status;
        row.LastError = message;
        row.UpdatedAt = now;
        row.NextAttemptAt = status is ArrRegrabStatus.Requested or ArrRegrabStatus.SearchWithheld
            ? now + ReplacementCheckInterval
            : null;
        if (status is ArrRegrabStatus.Requested or ArrRegrabStatus.SearchWithheld)
            row.RequestedAt = now;
        else
            row.CompletedAt = now;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        var app = AppName(row.ArrMediaKind);
        var why = row.Reason ?? "requested";
        switch (status)
        {
            case ArrRegrabStatus.Requested:
                Log.Information("Requested regrab for {Release} in {App} ({Reason}){Blocklist}",
                    row.ReleaseName, app, why, row.Blocklisted ? "; the old release was blocklisted" : "");
                break;
            case ArrRegrabStatus.SearchWithheld:
                Log.Warning("Regrab for {Release}: {App} removed the file ({Reason}), but the replacement search was withheld by the per-item search limit",
                    row.ReleaseName, app, why);
                break;
            case ArrRegrabStatus.Replaced:
                Log.Information("Regrab for {Release} completed: {App} imported a replacement", row.ReleaseName, app);
                break;
            default:
                Log.Warning("Regrab for {Release} {Outcome} ({Reason}). Reason: {Message}",
                    row.ReleaseName, status == ArrRegrabStatus.Skipped ? "skipped" : "failed", why, message);
                break;
        }
    }

    private static async Task SaveQuietlyAsync(DavDatabaseContext ctx)
    {
        try
        {
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            Log.Debug(e, "Could not persist regrab state during shutdown");
        }
    }

    internal static readonly TimeSpan ReplacementCheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Marks requested regrabs as replaced once Sonarr/Radarr reports a different file for the
    /// item, or a new symlink appears at the removed library path.
    /// </summary>
    public async Task<int> CheckReplacementsAsync(int limit, CancellationToken ct)
    {
        await using var ctx = CreateContext();
        var now = UtcNow;
        var rows = await ctx.ArrRegrabRequests
            .Where(x => (x.Status == ArrRegrabStatus.Requested || x.Status == ArrRegrabStatus.SearchWithheld)
                        && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.NextAttemptAt)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        var replaced = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            row.NextAttemptAt = now + ReplacementCheckInterval;
            if (row.LibraryPath is not null && row.LinkRemoved)
            {
                var target = SafeLinkTarget(row.LibraryPath);
                if (target is not null && !string.Equals(target, row.PreviousLinkTarget, StringComparison.Ordinal))
                {
                    await FinishAsync(ctx, row, ArrRegrabStatus.Replaced, null, ct).ConfigureAwait(false);
                    replaced++;
                    continue;
                }
            }

            var match = MatchFrom(row);
            var client = match is null
                ? null
                : ArrClients().FirstOrDefault(c =>
                    string.Equals(c.Host.TrimEnd('/'), row.ArrHost?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (client is null || match is null || _backoff.IsInBackoff(client.Host))
                continue;
            try
            {
                var current = await client.GetCurrentMediaFileIdAsync(match, ct).ConfigureAwait(false);
                _backoff.RecordSuccess(client.Host);
                if (current is not null && current != match.FileId)
                {
                    await FinishAsync(ctx, row, ArrRegrabStatus.Replaced, null, ct).ConfigureAwait(false);
                    replaced++;
                }
            }
            catch (Exception e) when (IsArrFailure(e, ct))
            {
                _backoff.RecordFailure(client.Host, e);
                Log.Debug(e, "Regrab replacement check failed for {Release}", row.ReleaseName);
            }
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return replaced;
    }

    private static string? SafeLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- health repair

    /// <summary>
    /// Records a replacement that health repair already performed through Arr, so the UI shows
    /// the same "regrab requested" state for every source.
    /// </summary>
    public async Task RecordHealthRepairAsync(
        DavItem davItem,
        string linkedPath,
        string? arrHost,
        ArrMediaFileMatch? match,
        bool searchWithheld,
        CancellationToken ct)
    {
        try
        {
            await using var ctx = CreateContext();
            var key = $"dav:{davItem.Id:D}";
            var row = await ctx.ArrRegrabRequests.FirstOrDefaultAsync(x => x.DedupKey == key, ct).ConfigureAwait(false);
            var now = UtcNow;
            if (row is null)
            {
                row = new ArrRegrabRequest { Id = Guid.NewGuid(), DedupKey = key, CreatedAt = now };
                ctx.ArrRegrabRequests.Add(row);
            }

            ResetForNewAttempt(row, SourceHealthRepair, davItem.Name, "health repair found the release damaged", now);
            row.DavItemId = davItem.Id;
            row.LibraryPath = linkedPath;
            if (arrHost is not null && match is not null)
            {
                ApplyTarget(row, new ArrRegrabTarget(
                    match.Kind == ArrMediaKind.Movie ? "Radarr" : "Sonarr",
                    arrHost,
                    match.Kind == ArrMediaKind.Movie ? "movie" : "episode",
                    match.FileId,
                    match.MediaIds,
                    linkedPath));
            }

            row.ArrFileRemoved = true;
            row.Blocklisted = true;
            row.Attempts = 1;
            row.Status = searchWithheld ? ArrRegrabStatus.SearchWithheld : ArrRegrabStatus.Requested;
            row.RequestedAt = now;
            row.NextAttemptAt = now + ReplacementCheckInterval;
            row.LastError = searchWithheld ? "The replacement search was withheld by the per-item search limit." : null;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException && e is not OperationCanceledException)
        {
            // Health repair already completed in Arr; failing to record the UI state must not undo it.
            if (e.TryGetKnownErrorMessage(out var reason))
                Log.Warning("Could not record the health-repair regrab for {Path}. Reason: {Reason}", davItem.Path, reason);
            else
                Log.Error(e, "Could not record the health-repair regrab for {Path}", davItem.Path);
        }
    }

    // ---------------------------------------------------------------- lookups for UI

    public async Task<IReadOnlyDictionary<string, ArrRegrabRequestView>> GetActiveByKeysAsync(
        DavDatabaseContext ctx,
        IReadOnlyCollection<Guid> davItemIds,
        IReadOnlyCollection<string> absoluteLinkPaths,
        CancellationToken ct)
    {
        if (davItemIds.Count == 0 && absoluteLinkPaths.Count == 0)
            return new Dictionary<string, ArrRegrabRequestView>();
        var ids = davItemIds.ToArray();
        var paths = absoluteLinkPaths.ToArray();
        var rows = await ctx.ArrRegrabRequests.AsNoTracking()
            .Where(x => (x.DavItemId != null && ids.Contains(x.DavItemId.Value))
                        || (x.LibraryPath != null && paths.Contains(x.LibraryPath)))
            .ToListAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, ArrRegrabRequestView>(StringComparer.Ordinal);
        foreach (var row in rows.Where(x => ArrRegrabStatus.IsActive(x.Status)).OrderBy(x => x.UpdatedAt))
        {
            var view = ArrRegrabRequestView.From(row);
            if (row.DavItemId is { } id)
                result[$"dav:{id:D}"] = view;
            if (row.LibraryPath is not null)
                result[$"link:{row.LibraryPath}"] = view;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, ArrRegrabRequestView>> GetMigrationStatesAsync(
        IReadOnlyCollection<Guid> legacyIds,
        CancellationToken ct)
    {
        await using var ctx = CreateContext();
        var keys = legacyIds.Select(MigrationKey).ToArray();
        var rows = await ctx.ArrRegrabRequests.AsNoTracking()
            .Where(x => keys.Contains(x.DedupKey))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(row => LegacyIdFrom(row.DedupKey) is not null)
            .ToDictionary(row => LegacyIdFrom(row.DedupKey)!.Value, ArrRegrabRequestView.From);
    }

    // ---------------------------------------------------------------- migration failures

    internal static string MigrationKey(Guid legacyId) => $"migration:{legacyId:D}";

    internal static Guid? LegacyIdFrom(string dedupKey) =>
        dedupKey.StartsWith("migration:", StringComparison.Ordinal)
        && Guid.TryParse(dedupKey["migration:".Length..], out var id)
            ? id
            : null;

    /// <summary>
    /// Classifies a migration import failure reason. Only releases rejected as damaged on
    /// Usenet or for missing articles are regrabbed.
    /// </summary>
    public static string? ClassifyFailureReason(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;
        if (error.Contains("Release damaged on Usenet", StringComparison.OrdinalIgnoreCase))
            return "damaged on Usenet";
        if (error.Contains("Missing articles", StringComparison.OrdinalIgnoreCase))
            return "missing articles";
        return null;
    }

    internal MigrationRegrabCandidate Classify(
        MigrationFailureRecord failure,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, ArrRegrabRequest> existing)
    {
        if (!Guid.TryParse(failure.LegacyDavItemId, out var legacyId)
            || string.IsNullOrWhiteSpace(failure.LibraryRelativePath)
            || Path.IsPathRooted(failure.LibraryRelativePath)
            || failure.LibraryRelativePath.Split('/', '\\').Any(segment => segment is "" or "." or ".."))
            return new(failure, MigrationRegrabOutcome.InvalidRecord, null,
                "The failure record lacks a legacy item id or a safe library-relative path.");
        if (failure.SubmissionState is { } state && !string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
            return new(failure, MigrationRegrabOutcome.NotFailed, null,
                $"The submission is '{state}', not a confirmed import failure.");
        if (ClassifyFailureReason(failure.Reason) is null)
            return new(failure, MigrationRegrabOutcome.NotEligibleReason, null,
                "Only releases that failed as damaged on Usenet or for missing articles are regrabbed.");
        if (existing.TryGetValue(MigrationKey(legacyId), out var row) && row.Status != ArrRegrabStatus.Failed)
            return new(failure, MigrationRegrabOutcome.AlreadyRequested, row.LibraryPath,
                $"Regrab already recorded ({row.Status}).");
        if (roots.Count == 0)
            return new(failure, MigrationRegrabOutcome.OutsideRoots, null,
                "Configure the Library Directory (or a library scan directory) that holds the old library links.");

        var checks = roots
            .Select(root => Path.Join(root, failure.LibraryRelativePath.Replace('\\', '/')))
            .Select(path => LibrarySymlinkGuard.Inspect(path, roots, failure.OriginalTarget, legacyId))
            .ToArray();
        var symlinks = checks.Where(check => check.Kind == LibraryLinkInspection.Symlink).ToArray();
        if (symlinks.Length == 1)
            return new(failure, MigrationRegrabOutcome.Eligible, symlinks[0].Path,
                "The old library symlink still points at the failed legacy item.");
        if (symlinks.Length > 1)
            return new(failure, MigrationRegrabOutcome.Ambiguous, null,
                "The same relative path is a matching symlink under more than one library root.");
        if (checks.Any(check => check.Kind == LibraryLinkInspection.TargetMismatch))
            return new(failure, MigrationRegrabOutcome.SourceLinkChanged,
                checks.First(check => check.Kind == LibraryLinkInspection.TargetMismatch).Path,
                "The old library link now points somewhere else; it was probably replaced already.");
        if (checks.Any(check => check.Kind == LibraryLinkInspection.NotSymlink))
            return new(failure, MigrationRegrabOutcome.NotSymlink, null,
                "The old library entry is a regular file, not a symlink.");
        if (checks.Any(check => check.Kind is LibraryLinkInspection.SymlinkedParent or LibraryLinkInspection.InvalidPath))
            return new(failure, MigrationRegrabOutcome.OutsideRoots, null,
                checks.First(check => check.Kind is LibraryLinkInspection.SymlinkedParent or LibraryLinkInspection.InvalidPath).Message);
        return new(failure, MigrationRegrabOutcome.SourceLinkMissing, null,
            "The old library link no longer exists under any configured library directory.");
    }

    private static async Task<Dictionary<string, ArrRegrabRequest>> ExistingMigrationRowsAsync(
        DavDatabaseContext ctx,
        IEnumerable<MigrationFailureRecord> failures,
        CancellationToken ct)
    {
        var keys = failures
            .Select(failure => Guid.TryParse(failure.LegacyDavItemId, out var id) ? MigrationKey(id) : null)
            .OfType<string>()
            .Distinct()
            .ToArray();
        var rows = new List<ArrRegrabRequest>();
        foreach (var chunk in keys.Chunk(500))
        {
            rows.AddRange(await ctx.ArrRegrabRequests
                .Where(x => chunk.Contains(x.DedupKey))
                .ToListAsync(ct).ConfigureAwait(false));
        }

        return rows.ToDictionary(row => row.DedupKey, StringComparer.Ordinal);
    }

    /// <summary>
    /// Records a regrab for every damaged / missing-articles failure of a migration batch.
    /// Idempotent. Never calls Arr: the worker processes the rows, so a batch never waits on
    /// Sonarr/Radarr. Failures whose old library link cannot be verified are recorded as
    /// skipped so the batch ledger still shows an outcome.
    /// </summary>
    public async Task<int> EnqueueMigrationFailuresAsync(
        IReadOnlyList<MigrationFailureRecord> failures,
        CancellationToken ct)
    {
        if (failures.Count == 0)
            return 0;
        await using var ctx = CreateContext();
        var existing = await ExistingMigrationRowsAsync(ctx, failures, ct).ConfigureAwait(false);
        var roots = LibrarySymlinkGuard.ConfiguredRoots(_config);
        var added = 0;
        var now = UtcNow;
        foreach (var failure in failures)
        {
            var reason = ClassifyFailureReason(failure.Reason);
            if (reason is null || !Guid.TryParse(failure.LegacyDavItemId, out var legacyId))
                continue;
            if (existing.ContainsKey(MigrationKey(legacyId)))
                continue;
            var candidate = Classify(failure, roots, existing);
            if (candidate.Outcome is MigrationRegrabOutcome.NotFailed or MigrationRegrabOutcome.InvalidRecord)
                continue;
            var row = NewMigrationRow(failure, legacyId, reason, candidate, now);
            ctx.ArrRegrabRequests.Add(row);
            existing[row.DedupKey] = row;
            added++;
            if (row.Status == ArrRegrabStatus.Skipped)
                Log.Warning("Skipped regrab for {Release} ({Reason}). Reason: {Message}",
                    row.ReleaseName, reason, candidate.Message);
        }

        if (added > 0)
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return added;
    }

    private static ArrRegrabRequest NewMigrationRow(
        MigrationFailureRecord failure,
        Guid legacyId,
        string reason,
        MigrationRegrabCandidate candidate,
        DateTime now)
    {
        var eligible = candidate.Outcome == MigrationRegrabOutcome.Eligible;
        return new ArrRegrabRequest
        {
            Id = Guid.NewGuid(),
            DedupKey = MigrationKey(legacyId),
            Source = SourceMigration,
            Status = eligible ? ArrRegrabStatus.Pending : ArrRegrabStatus.Skipped,
            LibraryPath = candidate.LibraryPath,
            ExpectedLinkTarget = failure.OriginalTarget,
            ReleaseName = !string.IsNullOrWhiteSpace(failure.ReleaseName)
                ? failure.ReleaseName
                : Path.GetFileName(failure.LibraryRelativePath ?? legacyId.ToString()),
            Reason = $"migration import failed: {reason}",
            LastError = eligible ? null : candidate.Message,
            MigrationBatchIndex = failure.BatchIndex,
            MigrationSourceReleaseId = failure.SourceReleaseId,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = eligible ? null : now,
        };
    }

    /// <summary>Dry run for the one-off "Regrab failed migration imports" action. No mutation.</summary>
    public async Task<MigrationRegrabPreview> PreviewMigrationFailuresAsync(
        IReadOnlyList<MigrationFailureRecord> failures,
        CancellationToken ct)
    {
        await using var ctx = CreateContext();
        var candidates = await ClassifyAllAsync(ctx, failures, ct).ConfigureAwait(false);
        var fingerprint = Fingerprint(candidates);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        PrunePreviewTokens();
        _previewTokens[token] = (fingerprint, _time.GetUtcNow() + PreviewTokenLifetime);
        return new MigrationRegrabPreview(
            candidates.Count,
            candidates.Count(c => c.Outcome == MigrationRegrabOutcome.Eligible),
            candidates.Count(c => c.Outcome == MigrationRegrabOutcome.AlreadyRequested),
            candidates
                .Where(c => c.Outcome is not (MigrationRegrabOutcome.Eligible or MigrationRegrabOutcome.AlreadyRequested))
                .GroupBy(c => c.Outcome)
                .ToDictionary(group => group.Key, group => group.Count()),
            candidates,
            token);
    }

    /// <summary>
    /// Queues at most <paramref name="limit"/> eligible failures after a matching dry run.
    /// The worker processes them one at a time, rate-limited, and resumes after restarts.
    /// </summary>
    public async Task<(bool Ok, string Message, int Queued, int Remaining)> QueueMigrationFailuresAsync(
        IReadOnlyList<MigrationFailureRecord> failures,
        int limit,
        string? previewToken,
        CancellationToken ct)
    {
        if (limit is < 1 or > 500)
            return (false, "The limit must be between 1 and 500.", 0, 0);
        PrunePreviewTokens();
        if (previewToken is null || !_previewTokens.TryGetValue(previewToken, out var approval))
            return (false, "Run a dry run first; the approval is missing or expired.", 0, 0);

        await using var ctx = CreateContext();
        var candidates = await ClassifyAllAsync(ctx, failures, ct).ConfigureAwait(false);
        if (!string.Equals(Fingerprint(candidates), approval.Fingerprint, StringComparison.Ordinal))
            return (false, "The candidates changed since the dry run. Run the dry run again.", 0, 0);
        _previewTokens.TryRemove(previewToken, out _);

        var eligible = candidates.Where(c => c.Outcome == MigrationRegrabOutcome.Eligible).ToArray();
        var existing = await ExistingMigrationRowsAsync(ctx, failures, ct).ConfigureAwait(false);
        var now = UtcNow;
        var queued = 0;
        foreach (var candidate in eligible.Take(limit))
        {
            var legacyId = Guid.Parse(candidate.Failure.LegacyDavItemId!);
            var reason = ClassifyFailureReason(candidate.Failure.Reason)!;
            if (existing.TryGetValue(MigrationKey(legacyId), out var row))
            {
                // Only failed rows are eligible again; restart them from the beginning.
                ResetForNewAttempt(row, SourceMigration, row.ReleaseName, $"migration import failed: {reason}", now);
                row.LibraryPath = candidate.LibraryPath;
                row.ExpectedLinkTarget = candidate.Failure.OriginalTarget;
            }
            else
            {
                ctx.ArrRegrabRequests.Add(NewMigrationRow(candidate.Failure, legacyId, reason, candidate, now));
            }

            queued++;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Information("Queued {Queued} failed migration imports for regrab ({Remaining} eligible remain for a later run)",
            queued, eligible.Length - queued);
        return (true, $"Queued {queued} regrab request{(queued == 1 ? "" : "s")}.", queued, eligible.Length - queued);
    }

    private async Task<List<MigrationRegrabCandidate>> ClassifyAllAsync(
        DavDatabaseContext ctx,
        IReadOnlyList<MigrationFailureRecord> failures,
        CancellationToken ct)
    {
        var existing = await ExistingMigrationRowsAsync(ctx, failures, ct).ConfigureAwait(false);
        var roots = LibrarySymlinkGuard.ConfiguredRoots(_config);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MigrationRegrabCandidate>(failures.Count);
        foreach (var failure in failures
                     .OrderBy(f => f.BatchIndex ?? int.MaxValue)
                     .ThenBy(f => f.LibraryRelativePath, StringComparer.Ordinal))
        {
            if (failure.LegacyDavItemId is { } id && !seen.Add(id))
                continue; // the same failure listed twice (for example in two uploaded reports)
            result.Add(Classify(failure, roots, existing));
        }

        return result;
    }

    private static string Fingerprint(IEnumerable<MigrationRegrabCandidate> candidates)
    {
        var text = string.Join("\n", candidates
            .Where(c => c.Outcome == MigrationRegrabOutcome.Eligible)
            .Select(c => $"{c.Failure.LegacyDavItemId}|{c.LibraryPath}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void PrunePreviewTokens()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, approval) in _previewTokens)
        {
            if (approval.ExpiresAt <= now)
                _previewTokens.TryRemove(token, out _);
        }
    }

    public async Task<IReadOnlyDictionary<string, int>> GetMigrationStatusCountsAsync(CancellationToken ct)
    {
        await using var ctx = CreateContext();
        return await ctx.ArrRegrabRequests.AsNoTracking()
            .Where(x => x.Source == SourceMigration)
            .GroupBy(x => x.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
    }
}
