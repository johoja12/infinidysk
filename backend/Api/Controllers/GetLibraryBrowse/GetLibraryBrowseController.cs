using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Library;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Api.Controllers.GetLibraryBrowse;

[ApiController]
[Route("api/get-library-browse")]
[ProducesResponseType(typeof(LibraryBrowseResult), StatusCodes.Status200OK)]
public sealed class GetLibraryBrowseController(DavDatabaseClient dbClient,
    PlexLibraryMetadataService plexMetadata) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetLibraryBrowseRequest(HttpContext);
        var scanner = HttpContext.RequestServices.GetService<LibraryCatalogScanner>();
        var catalog = new LibraryCatalogService(dbClient.Ctx);
        var browse = new LibraryBrowseService(catalog, plexMetadata);
        var result = await browse.QueryAsync(request.Query, scanner, request.CancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }
}

public sealed class GetLibraryBrowseRequest
{
    private static readonly HashSet<string> AllowedCategories =
        new(StringComparer.OrdinalIgnoreCase) { "shows", "movies", "unmatched" };
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "all", "internal", "external", "broken" };

    public LibraryBrowseQuery Query { get; }
    public CancellationToken CancellationToken { get; }

    public GetLibraryBrowseRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();
        var category = context.GetQueryParam("category") ?? "shows";
        var type = context.GetQueryParam("type") ?? "all";
        if (!AllowedCategories.Contains(category)) errors.Add("category", "Invalid category parameter.");
        if (!AllowedTypes.Contains(type)) errors.Add("type", "Invalid type parameter.");
        var search = context.GetQueryParam("q");
        if (search is { Length: > 200 }) errors.Add("q", "Search query is too long.");
        var group = context.GetQueryParam("group");
        if (group is { Length: > 500 }) errors.Add("group", "Group key is too long.");
        var page = 1;
        var groupPage = 1;
        if (errors.TryParseInt("page", context.GetQueryParam("page"), "Invalid page parameter", out var parsedPage))
            page = Math.Max(1, parsedPage);
        if (errors.TryParseInt("groupPage", context.GetQueryParam("groupPage"), "Invalid groupPage parameter", out var parsedGroupPage))
            groupPage = Math.Max(1, parsedGroupPage);
        errors.ThrowIfAny();

        Query = new LibraryBrowseQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Category = category.ToLowerInvariant(),
            TypeFilter = type.ToLowerInvariant(),
            Page = page,
            GroupKey = group,
            GroupPage = groupPage,
        };
    }
}
