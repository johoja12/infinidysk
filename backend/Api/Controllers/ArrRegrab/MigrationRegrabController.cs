using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.Regrab;

namespace NzbWebDAV.Api.Controllers.ArrRegrab;

/// <summary>
/// One-off "Regrab failed migration imports" for rows already recorded as
/// <c>import-failed</c> by earlier batches.
/// GET  <c>/api/arr-regrab/migration-failures</c> — counts of migration regrab requests by state.
/// POST <c>/api/arr-regrab/migration-failures</c> with <c>{ failures, limit, previewToken }</c> —
/// queue at most <c>limit</c> eligible failures after a matching dry run. The background
/// worker processes them one at a time, rate-limited, and resumes after restarts.
/// </summary>
[ApiController]
[Route("api/arr-regrab/migration-failures")]
[ProducesResponseType(typeof(MigrationRegrabResponse), StatusCodes.Status200OK)]
public sealed class MigrationRegrabController(ArrRegrabService regrab) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
        {
            var counts = await regrab.GetMigrationStatusCountsAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new MigrationRegrabResponse { Status = true, StatusCounts = counts });
        }

        var body = await MigrationRegrabBody.ReadAsync(HttpContext).ConfigureAwait(false);
        var (ok, message, queued, remaining) = await regrab.QueueMigrationFailuresAsync(
            body.Failures!, body.Limit ?? 25, body.PreviewToken, HttpContext.RequestAborted).ConfigureAwait(false);
        var response = new MigrationRegrabResponse
        {
            Status = ok,
            Error = ok ? null : message,
            Message = message,
            Queued = queued,
            Remaining = remaining,
        };
        return ok ? Ok(response) : Conflict(response);
    }
}

/// <summary>
/// POST <c>/api/arr-regrab/migration-failures/dry-run</c> with <c>{ failures }</c> — classify
/// the supplied import failures without changing anything and issue a 15-minute approval
/// token for the bounded run.
/// </summary>
[ApiController]
[Route("api/arr-regrab/migration-failures/dry-run")]
[ProducesResponseType(typeof(MigrationRegrabResponse), StatusCodes.Status200OK)]
public sealed class MigrationRegrabDryRunController(ArrRegrabService regrab) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var body = await MigrationRegrabBody.ReadAsync(HttpContext).ConfigureAwait(false);
        var preview = await regrab.PreviewMigrationFailuresAsync(body.Failures!, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new MigrationRegrabResponse
        {
            Status = true,
            Message = $"{preview.Eligible} of {preview.Total} failed imports can be regrabbed; " +
                      $"{preview.AlreadyRequested} already have a regrab.",
            Total = preview.Total,
            Eligible = preview.Eligible,
            AlreadyRequested = preview.AlreadyRequested,
            Skipped = preview.Skipped,
            PreviewToken = preview.PreviewToken,
            Candidates = preview.Candidates
                .Select(c => new MigrationRegrabResponse.CandidateDto
                {
                    LegacyDavItemId = c.Failure.LegacyDavItemId,
                    LibraryRelativePath = c.Failure.LibraryRelativePath,
                    BatchIndex = c.Failure.BatchIndex,
                    Outcome = c.Outcome,
                    LibraryPath = c.LibraryPath,
                    Message = c.Message,
                })
                .ToList(),
        });
    }
}

public sealed class MigrationRegrabBody
{
    internal const int MaxFailures = 20_000;

    public List<MigrationFailureRecord>? Failures { get; init; }
    public int? Limit { get; init; }
    public string? PreviewToken { get; init; }

    internal static async Task<MigrationRegrabBody> ReadAsync(HttpContext context)
    {
        MigrationRegrabBody? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<MigrationRegrabBody>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("The request body must be JSON with a failures array.");
        }

        if (body?.Failures is null || body.Failures.Count == 0)
            throw new BadHttpRequestException("Provide at least one failed import in 'failures'.");
        if (body.Failures.Count > MaxFailures)
            throw new BadHttpRequestException($"Provide at most {MaxFailures} failed imports per request.");
        return body;
    }
}

public sealed class MigrationRegrabResponse : BaseApiResponse
{
    public string? Message { get; init; }
    public int? Total { get; init; }
    public int? Eligible { get; init; }
    public int? AlreadyRequested { get; init; }
    public IReadOnlyDictionary<string, int>? Skipped { get; init; }
    public string? PreviewToken { get; init; }
    public List<CandidateDto>? Candidates { get; init; }
    public int? Queued { get; init; }
    public int? Remaining { get; init; }
    public IReadOnlyDictionary<string, int>? StatusCounts { get; init; }

    public sealed class CandidateDto
    {
        public string? LegacyDavItemId { get; init; }
        public string? LibraryRelativePath { get; init; }
        public int? BatchIndex { get; init; }
        public required string Outcome { get; init; }
        public string? LibraryPath { get; init; }
        public required string Message { get; init; }
    }
}
