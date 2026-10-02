using System.Net;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbDavMigration.Recovery;

// This state belongs to the temporary migration journal, not application settings.
internal sealed record ArrCleanupTarget(string InstanceKey, string Path, ArrMediaFileMatch Match);
internal sealed record ArrCleanupState(string Stage = "delete", int[]? PreviousCommands = null,
    DateTimeOffset? SearchStarted = null, int? CommandId = null);

internal class MigrationArrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    public virtual Task<JsonElement?> ReadAsync(string path, CancellationToken ct) => ReadCoreAsync(path, ct);
    private async Task<JsonElement?> ReadCoreAsync(string path, CancellationToken ct)
    {
        var document = await GetOrNull<JsonDocument>(path, ct).ConfigureAwait(false);
        using (document) return document?.RootElement.Clone();
    }
    public virtual Task<HttpStatusCode> DeleteAsync(string path, CancellationToken ct) => Delete(path, ct: ct);
    public virtual Task<ArrCommand> SearchAsync(object command, CancellationToken ct) => CommandAsync(command, ct);
}

internal static class ArrCleanupRecovery
{
    internal static bool Transient(Exception e, CancellationToken ct) => !ct.IsCancellationRequested
        && (e is IOException or TaskCanceledException
            || e is HttpRequestException h && (h.StatusCode is null || h.StatusCode == HttpStatusCode.RequestTimeout
                || h.StatusCode == HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500));

    internal static async Task<T> ReadWithRetryAsync<T>(Func<Task<T>> read, CancellationToken ct,
        Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Backoff;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await read().ConfigureAwait(false); }
            catch (Exception e) when (attempt < 4 && Transient(e, ct))
            { await delay(attempt, ct).ConfigureAwait(false); }
        }
    }

    private static Task Backoff(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);

    internal static async Task RunAsync(MigrationArrClient client, ArrCleanupTarget target,
        ArrCleanupState state, Func<ArrCleanupState, Task> save, CancellationToken ct,
        Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Backoff;
        var match = target.Match;
        if (match.Kind is not (ArrMediaKind.Episode or ArrMediaKind.Movie) || match.MediaIds.Count == 0 || match.MediaIds.Any(id => id <= 0)
            || match.MediaIds.Distinct().Count() != match.MediaIds.Count
            || state.Stage is not ("delete" or "search_ready" or "search_started" or "completed"))
            throw new InvalidDataException("Invalid persisted Arr cleanup identity or stage.");
        if (state.Stage == "completed") return;
        var fileEndpoint = match.Kind == ArrMediaKind.Episode ? "episodefile" : "moviefile";
        async Task<JsonElement?> Read(string path) => await ReadWithRetryAsync(
            () => client.ReadAsync(path, ct), ct, delay).ConfigureAwait(false);
        async Task AssertNoReplacement()
        {
            foreach (var id in match.MediaIds)
            {
                var media = await Read($"/{(match.Kind == ArrMediaKind.Episode ? "episode" : "movie")}/{id}")
                    .ConfigureAwait(false);
                if (media is null || !media.Value.TryGetProperty(
                        match.Kind == ArrMediaKind.Episode ? "episodeFileId" : "movieFileId", out var fileId)
                    || fileId.GetInt32() != 0)
                    throw new InvalidDataException("Arr media changed after removal; preserve its replacement and stop cleanup.");
            }
        }
        if (state.Stage == "delete")
        {
            if (match.FileId <= 0) throw new InvalidDataException("Missing persisted Arr file ID.");
            for (var attempt = 1; ; attempt++)
            {
                // Always read first, including after a timeout or process restart.
                var file = await Read($"/{fileEndpoint}/{match.FileId}").ConfigureAwait(false);
                if (file is null) break;
                if (!file.Value.TryGetProperty("path", out var path) || path.GetString() != target.Path)
                    throw new InvalidDataException("Arr file identity changed; refusing deletion.");
                foreach (var id in match.MediaIds)
                {
                    var media = await Read($"/{(match.Kind == ArrMediaKind.Episode ? "episode" : "movie")}/{id}")
                        .ConfigureAwait(false);
                    if (media is null || media.Value.GetProperty(
                            match.Kind == ArrMediaKind.Episode ? "episodeFileId" : "movieFileId").GetInt32() != match.FileId)
                        throw new InvalidDataException("Arr file no longer owns the recorded media identities.");
                }
                try
                {
                    var status = await client.DeleteAsync($"/{fileEndpoint}/{match.FileId}", ct).ConfigureAwait(false);
                    if (status != HttpStatusCode.NotFound && (int)status is not (>= 200 and < 300))
                        throw new HttpRequestException("Arr deletion failed.", null, status);
                }
                catch (Exception e) when (Transient(e, ct))
                { Console.WriteLine($"Arr deletion needs recheck (attempt {attempt}/4)."); }
                // A timeout can mean successful deletion. Recheck even on the last attempt.
                if (await Read($"/{fileEndpoint}/{match.FileId}").ConfigureAwait(false) is null) break;
                if (attempt == 4) throw new IOException("Arr deletion remains pending after four verified attempts.");
                await delay(attempt, ct).ConfigureAwait(false);
            }
            await AssertNoReplacement().ConfigureAwait(false);
            state = state with { Stage = "search_ready" };
            await save(state).ConfigureAwait(false);
        }
        var commandName = match.Kind == ArrMediaKind.Episode ? "EpisodeSearch" : "MoviesSearch";
        var idProperty = match.Kind == ArrMediaKind.Episode ? "episodeIds" : "movieIds";
        async Task<JsonElement[]> Commands()
        {
            var commands = await Read("/command").ConfigureAwait(false);
            if (commands is null || commands.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Arr command list is unavailable.");
            return commands.Value.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        if (state.Stage == "search_ready")
        {
            await AssertNoReplacement().ConfigureAwait(false);
            var previous = await Commands().ConfigureAwait(false);
            state = state with { Stage = "search_started", SearchStarted = DateTimeOffset.UtcNow,
                PreviousCommands = previous.Select(x => x.GetProperty("id").GetInt32()).ToArray() };
            await save(state).ConfigureAwait(false); // Never repeat an uncertain search POST.
            try
            {
                var command = await client.SearchAsync(new Dictionary<string, object>
                    { ["name"] = commandName, [idProperty] = match.MediaIds }, ct).ConfigureAwait(false);
                if (command.Id <= 0) throw new InvalidDataException("Arr search returned no command ID.");
                if (command.Status is "failed" or "aborted" or "cancelled")
                    throw new InvalidOperationException("Arr replacement search command failed; reconcile before resuming.");
                await save(state with { Stage = "completed", CommandId = command.Id }).ConfigureAwait(false);
                return; // An acknowledged command ID proves acceptance, even if history expires quickly.
            }
            catch (Exception e) when (Transient(e, ct))
            { Console.WriteLine("Arr search response uncertain; rechecking accepted commands."); }
        }
        if (state.SearchStarted is null || state.PreviousCommands is null)
            throw new InvalidDataException("Search recovery is missing its durable command boundary.");
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var commands = await Commands().ConfigureAwait(false);
            var candidates = commands.Where(c =>
                !state.PreviousCommands.Contains(c.GetProperty("id").GetInt32())
                && (state.CommandId is null || c.GetProperty("id").GetInt32() == state.CommandId)
                && c.GetProperty("name").GetString() == commandName
                && c.TryGetProperty("body", out var body) && body.TryGetProperty(idProperty, out var ids)
                && ids.EnumerateArray().Select(x => x.GetInt32()).Order().SequenceEqual(match.MediaIds.Order())
                && c.TryGetProperty("queued", out var queued)
                && DateTimeOffset.TryParse(queued.GetString(), out var queuedAt)
                && queuedAt >= state.SearchStarted.Value.AddSeconds(-5)).ToArray();
            if (candidates.Length > 1) throw new InvalidOperationException("Search has an uncertain external outcome: multiple matching commands.");
            if (candidates.Length == 1)
            {
                var command = candidates[0];
                if (command.GetProperty("status").GetString() is "failed" or "aborted" or "cancelled")
                    throw new InvalidOperationException("Arr replacement search command failed; reconcile before resuming.");
                await save(state with { Stage = "completed", CommandId = command.GetProperty("id").GetInt32() })
                    .ConfigureAwait(false);
                return;
            }
            if (attempt < 4) await delay(attempt, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Search has an uncertain external outcome; no accepted command found after rechecking. Preserve the journal before resuming.");
    }
}
