using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Regrab;

namespace NzbWebDAV.Api.Controllers.ArrRegrab;

/// <summary>
/// GET  <c>/api/arr-regrab?davItemId=…|linkPath=…</c> — what a Regrab would do (release,
/// Sonarr/Radarr target, or why it is unavailable) plus any existing request state.
/// POST <c>/api/arr-regrab</c> with <c>{ davItemId?, linkPath? }</c> — request a regrab: remove
/// the library symlink (journaled), remove the Arr file record (blocklisting the release when
/// its download is known), and request a replacement search. Idempotent per file.
/// </summary>
[ApiController]
[Route("api/arr-regrab")]
[ProducesResponseType(typeof(ArrRegrabResponse), StatusCodes.Status200OK)]
public sealed class ArrRegrabController(ArrRegrabService regrab) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpMethods.IsPost(HttpContext.Request.Method))
        {
            ArrRegrabRequestBody? body;
            try
            {
                body = await HttpContext.Request
                    .ReadFromJsonAsync<ArrRegrabRequestBody>(HttpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new BadHttpRequestException("The request body must be JSON.");
            }

            var (davItemId, linkPath) = Parse(body?.DavItemId, body?.LinkPath);
            var result = await regrab.RequestAsync(davItemId, linkPath, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (result is null)
                return NotFound(new BaseApiResponse { Status = false, Error = "File not found." });
            var response = new ArrRegrabResponse
            {
                Status = result.Accepted,
                Error = result.Accepted ? null : result.Message,
                Message = result.Message,
                Request = result.Request,
            };
            return result.Accepted ? Ok(response) : Conflict(response);
        }

        var (id, path) = Parse(HttpContext.GetQueryParam("davItemId"), HttpContext.GetQueryParam("linkPath"));
        var preview = await regrab.PreviewAsync(id, path, HttpContext.RequestAborted).ConfigureAwait(false);
        if (preview is null)
            return NotFound(new BaseApiResponse { Status = false, Error = "File not found." });
        return Ok(new ArrRegrabResponse
        {
            Status = true,
            Eligible = preview.Eligible,
            DisabledReason = preview.DisabledReason,
            ReleaseName = preview.ReleaseName,
            LibraryPath = preview.LibraryPath,
            OldLibraryLink = preview.OldLibraryLink,
            Target = preview.Target is null
                ? null
                : new ArrRegrabResponse.TargetDto
                {
                    App = preview.Target.App,
                    Host = ArrRegrabTarget.DisplayHost(preview.Target.Host),
                    MediaKind = preview.Target.MediaKind,
                    MediaIds = preview.Target.MediaIds,
                    FileId = preview.Target.FileId,
                    Label = preview.Target.Label,
                },
            Request = preview.Request,
        });
    }

    private static (Guid? DavItemId, string? LinkPath) Parse(string? davItemId, string? linkPath)
    {
        if (!string.IsNullOrWhiteSpace(davItemId))
        {
            if (!Guid.TryParse(davItemId, out var id))
                throw new BadHttpRequestException("Invalid davItemId (use a UUID).");
            return (id, null);
        }

        if (string.IsNullOrWhiteSpace(linkPath))
            throw new BadHttpRequestException("A davItemId or linkPath is required.");
        return (null, linkPath);
    }
}

public sealed class ArrRegrabRequestBody
{
    public string? DavItemId { get; init; }
    public string? LinkPath { get; init; }
}

public sealed class ArrRegrabResponse : BaseApiResponse
{
    public string? Message { get; init; }
    public bool? Eligible { get; init; }
    public string? DisabledReason { get; init; }
    public string? ReleaseName { get; init; }
    public string? LibraryPath { get; init; }
    public bool? OldLibraryLink { get; init; }
    public TargetDto? Target { get; init; }
    public ArrRegrabRequestView? Request { get; init; }

    public sealed class TargetDto
    {
        public required string App { get; init; }
        public required string Host { get; init; }
        public required string MediaKind { get; init; }
        public required IReadOnlyList<int> MediaIds { get; init; }
        public int FileId { get; init; }
        public required string Label { get; init; }
    }
}
