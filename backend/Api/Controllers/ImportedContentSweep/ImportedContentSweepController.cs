using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ImportedContentSweep;

/// <summary>Progress and results of the imported-content verification sweep.</summary>
[ApiController]
[Route("api/imported-content-sweep")]
[ProducesResponseType(typeof(ImportedContentSweepState), StatusCodes.Status200OK)]
public sealed class ImportedContentSweepController(ImportedContentSweepService sweep) : GetOnlyApiController
{
    protected override Task<IActionResult> HandleRequest() =>
        Task.FromResult<IActionResult>(Ok(sweep.GetState()));
}

/// <summary>
/// Starts or resumes the imported-content verification sweep. A different category or
/// <c>restart</c> starts over; otherwise a paused sweep continues where it stopped.
/// </summary>
[ApiController]
[Route("api/imported-content-sweep/start")]
[RequestSizeLimit(16 * 1024)]
[ProducesResponseType(typeof(ImportedContentSweepState), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ImportedContentSweepState), StatusCodes.Status409Conflict)]
public sealed class StartImportedContentSweepController(ImportedContentSweepService sweep) : PostOnlyApiController
{
    public sealed record StartRequest(string? Category, int? Limit, bool Restart = false);

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = HttpContext.Request.ContentLength is > 0
            ? await HttpContext.Request.ReadFromJsonAsync<StartRequest>(HttpContext.RequestAborted)
                .ConfigureAwait(false)
            : null;
        request ??= new StartRequest(null, null);
        return sweep.Start(request.Category, request.Limit, request.Restart)
            ? Ok(sweep.GetState())
            : Conflict(sweep.GetState());
    }
}

/// <summary>Stops a running imported-content sweep; it can be resumed later.</summary>
[ApiController]
[Route("api/imported-content-sweep/stop")]
[ProducesResponseType(typeof(ImportedContentSweepState), StatusCodes.Status200OK)]
public sealed class StopImportedContentSweepController(ImportedContentSweepService sweep) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await sweep.StopSweepAsync().ConfigureAwait(false);
        return Ok(sweep.GetState());
    }
}
