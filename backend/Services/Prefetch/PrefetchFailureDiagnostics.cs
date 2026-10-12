using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Services.Prefetch;

internal static class PrefetchFailureDiagnostics
{
    // Exception messages and inner exceptions can contain credentials, URLs and
    // media paths. Persist/log only stable guidance, type names and job identity.
    /// <summary>A stable category (stored as the job's failure code) and safe operator guidance.</summary>
    internal static (string Category, string Guidance) Classify(Exception exception) => exception switch
        {
            CircuitAdmissionRejectedException => ("provider-unavailable", "Provider access is temporarily unavailable while circuits recover."),
            UsenetArticleNotFoundException => ("source-unavailable", "A required article is unavailable. Check source health before retrying."),
            SeekPositionNotFoundException => ("source-layout", "A source byte position could not be resolved. Check the imported source before retrying."),
            UnauthorizedAccessException => ("storage-access", "A file or cache access was denied. Check storage permissions before retrying."),
            OperationCanceledException => ("unexpected-cancellation", "A source operation was cancelled unexpectedly. Retry the job; inspect the correlated log if it repeats."),
            IOException => ("source-or-cache-io", "Source or cache I/O failed. Check source health and storage availability."),
            // Only the plain type: warming validation throws it, while derived argument
            // exceptions from deeper code (out-of-range offsets, nulls) are bugs.
            ArgumentException when exception.GetType() == typeof(ArgumentException) => ("invalid-request", "The warming request or imported media is no longer valid. Review the item and warming limits."),
            _ => ("unexpected-failure", "Warming failed unexpectedly. Inspect the correlated diagnostic log before retrying.")
        };

    internal static string Report(ILogger logger, PrefetchJob job, Exception exception, bool retryable)
    {
        var (category, guidance) = Classify(exception);
        var exceptionType = exception.GetType().Name;
        logger.Warning("Smart Prefetch job {JobId} for item {ItemId}: {FailureCategory} ({ExceptionType}). {Reason} Retry policy: {RetryPolicy}",
            job.Id, job.ItemId, category, exceptionType, guidance, retryable ? "bounded automatic retry" : "explicit retry required");
        // Preserve code frames for unexpected bugs without serializing the
        // exception itself, its message, Data, or secret-bearing inner exceptions.
        // Unexpected failures are bugs, so their frames reach operators' logs.
        if (category == "unexpected-failure")
            logger.Error("Smart Prefetch job {JobId} unexpected {ExceptionType} code frames: {StackTrace}", job.Id, exceptionType, exception.StackTrace);
        else if (category == "unexpected-cancellation")
            logger.Debug("Smart Prefetch job {JobId} exception code frames: {StackTrace}", job.Id, exception.StackTrace);
        return $"[{category}/{exceptionType}] {guidance} " +
            (retryable ? "Verified coverage retained; automatic retries are bounded." : "Verified coverage retained; retry explicitly.");
    }
}
