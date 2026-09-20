using System.Diagnostics;

namespace NzbDavMigration.Canary;

public sealed record CanaryValidationRead(string Position, long Offset, int BytesRead);

public sealed record CanaryValidationResult(
    string LibraryRelativePath,
    bool Success,
    long ExpectedFileSize,
    long? ActualFileSize,
    IReadOnlyList<CanaryValidationRead> Reads,
    string? FfprobeOutput,
    string? Error);

public sealed class CanaryValidator
{
    public async Task<IReadOnlyList<CanaryValidationResult>> ValidateAsync(
        string journalPath,
        int maximumBytesPerRead = 64 * 1024,
        TimeSpan? timeout = null,
        string? ffprobePath = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytesPerRead <= 0 || maximumBytesPerRead > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytesPerRead));
        var operationTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var journal = await CanaryJournalStore.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false)
                      ?? throw new FileNotFoundException("Canary apply journal is missing.", journalPath);
        var results = new List<CanaryValidationResult>();
        foreach (var link in journal.Links)
        {
            var reads = new List<CanaryValidationRead>();
            try
            {
                var linkInfo = new FileInfo(link.LinkPath);
                if (!CanaryPathSafety.PathExistsNoFollow(link.LinkPath)
                    || linkInfo.LinkTarget != link.TargetPath)
                    throw new InvalidDataException("Journaled link target no longer matches.");
                var target = new FileInfo(link.TargetPath);
                if (!target.Exists || target.Length != link.ExpectedFileSize)
                    throw new InvalidDataException("Journaled target size no longer matches.");
                reads.AddRange(await ReadWindowsAsync(
                    link.TargetPath, target.Length, maximumBytesPerRead,
                    operationTimeout, cancellationToken).ConfigureAwait(false));
                var ffprobe = ffprobePath is null
                    ? null
                    : await RunFfprobeAsync(ffprobePath, link.TargetPath, operationTimeout, cancellationToken)
                        .ConfigureAwait(false);
                results.Add(new CanaryValidationResult(
                    link.LibraryRelativePath, true, link.ExpectedFileSize, target.Length, reads, ffprobe, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                results.Add(new CanaryValidationResult(
                    link.LibraryRelativePath, false, link.ExpectedFileSize, null, reads, null, exception.Message));
            }
        }
        return results;
    }

    private static async Task<IReadOnlyList<CanaryValidationRead>> ReadWindowsAsync(
        string path,
        long length,
        int maximumBytesPerRead,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            maximumBytesPerRead, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var reads = new List<CanaryValidationRead>();
        var positions = new[]
        {
            (Name: "beginning", Offset: 0L),
            (Name: "middle", Offset: Math.Max(0, length / 2 - maximumBytesPerRead / 2)),
            (Name: "end", Offset: Math.Max(0, length - maximumBytesPerRead)),
        };
        foreach (var position in positions)
        {
            stream.Seek(position.Offset, SeekOrigin.Begin);
            var requested = (int)Math.Min(maximumBytesPerRead, length - position.Offset);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(operationTimeout);
            var buffer = new byte[requested];
            var read = await stream.ReadAsync(buffer, bounded.Token).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException($"No data returned at {position.Name}.");
            reads.Add(new CanaryValidationRead(position.Name, position.Offset, read));
        }
        return reads;
    }

    private static async Task<string> RunFfprobeAsync(
        string executable,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-show_format");
        process.StartInfo.ArgumentList.Add("-show_streams");
        process.StartInfo.ArgumentList.Add(path);
        if (!process.Start())
            throw new IOException("Unable to start ffprobe.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var error = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"ffprobe failed: {stderr.Trim()}");
            return stdout;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
