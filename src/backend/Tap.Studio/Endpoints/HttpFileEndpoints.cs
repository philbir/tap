using Tap.Studio.Contracts;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/http/*</c> — the one thing a <c>.http</c> file needs that no other kind does.
///
/// <para>Every other kind is edited as a structured spec, so the editor always knows what it is
/// holding. A <c>.http</c> file is edited as raw text, and the requests inside it are discovered
/// by parsing — which means an unsaved edit and the workspace model disagree from the first
/// keystroke. Adding a request, renaming one, or editing a request line (names are derived from
/// the URL when the file doesn't give one) all change which requests exist and what they are
/// called.</para>
///
/// <para>So the editor asks the server to parse what it is holding, rather than guessing with a
/// regex of its own. The list the user clicks Send on and the request the server then executes
/// come from the same parser reading the same text, so they cannot disagree about which request
/// is which.</para>
/// </summary>
public static class HttpFileEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/http/parse", (ParseHttpFileDto body) =>
        {
            var path = (body.Path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(path))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-path", "Path is required.", null, null));
            if (!KindResolver.IsHttpFileName(path))
                return Results.BadRequest(new WorkspaceErrorDto(
                    WorkspaceErrorCode.E_KIND_MISMATCH, $"'{path}' is not a .http file.", path, null));

            var parsed = HttpFileParser.Parse(path, body.Content ?? string.Empty);

            var requests = new List<HttpRequestSummaryDto>(parsed.Requests.Count);
            foreach (var request in parsed.Requests)
            {
                // The block re-parses without throwing: HttpFileParser only emits a RequestFile
                // once HttpBlockParser has already accepted its block.
                var block = HttpBlockParser.Parse(request.HttpBlock);
                requests.Add(new HttpRequestSummaryDto(
                    Path: request.RelativePath,
                    Name: request.Name ?? HttpFragment.Split(request.RelativePath).Fragment ?? request.RelativePath,
                    Method: block.Method,
                    Url: block.Url,
                    Line: request.HttpBlockStartLine));
            }

            return Results.Ok(new ParseHttpFileResultDto(
                requests,
                parsed.Errors
                    .Select(e => new WorkspaceErrorDto(
                        e.Code, e.Message, e.RelativePath, e.Line, e.Severity.ToString().ToLowerInvariant()))
                    .ToArray()));
        });
    }
}
