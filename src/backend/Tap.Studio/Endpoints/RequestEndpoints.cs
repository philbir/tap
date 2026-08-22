using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/requests*</c> — list, structured-read, render, and typed save. The on-disk
/// HTTP block is parsed into <see cref="HttpHeaderSpecDto"/> + method/url/body so the
/// client never deals with the fenced syntax; the server is the sole producer of the
/// canonical file content via <see cref="RequestSpecEmitter"/>.
/// </summary>
public static class RequestEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/requests");

        g.MapGet("/", (WorkspaceService svc) =>
        {
            var summaries = svc.Current.Requests.Select(r => new RequestSummaryDto(
                Path: r.RelativePath,
                Name: r.Name ?? Path.GetFileNameWithoutExtension(r.RelativePath),
                Id: r.Id,
                Auth: r.Auth?.RelativePath,
                Tags: r.Tags)).ToArray();
            return Results.Ok((IReadOnlyList<RequestSummaryDto>)summaries);
        });

        g.MapGet("/{*path}", (string path, WorkspaceService svc) =>
        {
            if (svc.Current.FindByPath(path) is not RequestFile req)
                return Results.NotFound();

            // Parse the http block into structured fields so the React side gets ready-to-edit
            // data. Markdown body is whatever sits *outside* the fenced block — strip the
            // fence to keep the description tidy in the editor.
            var parsed = TryParseHttpBlock(req.HttpBlock);
            var markdownBody = StripHttpFence(req.Body);

            var dto = new RequestDetailDto(
                Path: req.RelativePath,
                Name: req.Name ?? Path.GetFileNameWithoutExtension(req.RelativePath),
                Id: req.Id,
                Auth: req.Auth?.RelativePath,
                Tags: req.Tags,
                Method: parsed?.Method ?? "GET",
                Url: parsed?.Url ?? string.Empty,
                Headers: parsed?.Headers.Select(h => new HttpHeaderSpecDto(h.Key, h.Value)).ToArray() ?? [],
                RequestBody: parsed?.Body,
                Body: markdownBody,
                Vars: req.Vars,
                Source: svc.ReadSource(req.RelativePath),
                Protocol: req.Protocol.ToWire(),
                Transport: ToDto(req.Transport),
                Assertions: AssertSpecMapper.ToDto(req.Assertions),
                History: HistoryOptionsMapper.ToDto(req.History),
                EffectiveHistory: HistoryOptionsMapper.Effective(HistoryOptions.Resolve(
                    svc.Current.HistoryDefaults,
                    CollectionLocator.ForRequest(svc.Current, req)?.History,
                    req.History)));
            return Results.Ok(dto);
        });

        // Typed save — the only write endpoint. The client ships a structured RequestSpecDto;
        // the server emits the canonical YAML + http block. The legacy raw-PUT endpoint is
        // intentionally not registered.
        g.MapPut("/spec", (RequestSpecDto spec, WorkspaceService svc) =>
        {
            try
            {
                // Assigned here rather than only inside the emitter so the id can travel back on
                // the response: a request created and Sent in the same breath must already know
                // its own id, or history has nothing to file the exchange under.
                var id = SpecIds.Ensure(spec.Id);
                svc.Save(spec.Path, RequestSpecEmitter.ToFileSource(spec with { Id = id }));
                // A create-then-open client (sidebar "New request", the create dialog, duplicate)
                // refetches the request the moment this returns — the watcher's debounced reload
                // would lose that race and the GET would 404.
                svc.ReloadNow();
                return Results.Ok(new SavedSpecDto(id));
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
        });

        // Render takes the request path in the body because ASP.NET routing requires
        // catch-all parameters to be the last segment in the template; we can't suffix '/render'.
        app.MapPost("/api/render", async (RenderRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            try
            {
                var rendered = await svc.RenderAsync(body.Path, body.Env, body.Overrides, ct, body.Stage).ConfigureAwait(false);
                return Results.Ok(new RenderedRequestDto(
                    Method: rendered.Method,
                    Url: rendered.Url,
                    Headers: rendered.Headers,
                    Body: rendered.Body,
                    VariablesUsed: rendered.Metadata.VariablesUsed
                        .Select(s => new VariableTraceDto(s.ProviderName, s.Name, s.Resolved, s.IsSecret, s.Duration.TotalMilliseconds))
                        .ToArray(),
                    Stage: rendered.Metadata.StageName,
                    Protocol: rendered.Protocol.ToWire()));
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
        });
    }

    private static HttpBlockParser.Parsed? TryParseHttpBlock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return HttpBlockParser.Parse(raw); }
        catch { return null; }
    }

    /// <summary>
    /// Strip the ```http fenced block out of the body so the markdown description shown in
    /// the editor doesn't double up. We only strip the *first* http fence — extra ones, if
    /// any, are documentation and pass through verbatim.
    /// </summary>
    private static string StripHttpFence(string body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        var idx = body.IndexOf("```http", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return body.Trim();
        var close = body.IndexOf("```", idx + 7, StringComparison.Ordinal);
        if (close < 0) return body.Trim();
        var before = body[..idx].TrimEnd();
        var after = body[(close + 3)..].TrimStart('\r', '\n');
        var combined = string.IsNullOrWhiteSpace(before)
            ? after
            : (string.IsNullOrWhiteSpace(after) ? before : before + "\n\n" + after);
        return combined.Trim();
    }

    private static RequestTransportSettingsDto? ToDto(RequestTransportSettings settings)
        => settings.IgnoreTlsErrors is null && settings.TimeoutMs is null
            ? null
            : new RequestTransportSettingsDto(settings.IgnoreTlsErrors, settings.TimeoutMs);
}
