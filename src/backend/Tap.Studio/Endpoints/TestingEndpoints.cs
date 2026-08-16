using System.Text;
using System.Text.Json;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Execution.Contracts;
using Tap.Execution.Testing;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/flows*</c>, <c>/api/test-sets*</c>, and <c>POST /api/tests/run</c>.
///
/// <para>Reads and typed saves follow the same shape as every other kind: the client ships a
/// <c>*SpecDto</c> and the server emits the canonical YAML, so nothing but
/// <see cref="Specs"/> ever assembles a workspace file.</para>
///
/// <para>The run endpoint streams. A test set with ten entries against a real API takes real
/// time, and the difference between "a spinner" and "four passed, currently on the fifth" is
/// the difference between a usable runner and one people stop trusting.</para>
/// </summary>
public static class TestingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapFlows(app);
        MapTestSets(app);
        MapRun(app);
    }

    private static void MapFlows(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/flows");

        g.MapGet("/", (WorkspaceService svc) => Results.Ok(
            (IReadOnlyList<FlowSummaryDto>)svc.Current.Flows
                .Select(f => new FlowSummaryDto(f.RelativePath, DisplayName(f), f.Id, f.Steps.Count, f.Tags))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()));

        g.MapGet("/{*path}", (string path, WorkspaceService svc) =>
        {
            if (svc.Current.FindByPath(path) is not FlowFile flow) return Results.NotFound();
            return Results.Ok(new FlowDetailDto(
                Path: flow.RelativePath,
                Name: DisplayName(flow),
                Id: flow.Id,
                Vars: flow.Vars,
                Steps: TestingSpecMapper.ToDto(flow.Steps),
                Tags: flow.Tags,
                Body: flow.Body,
                Source: svc.ReadSource(flow.RelativePath)));
        });

        g.MapPut("/spec", (FlowSpecDto spec, WorkspaceService svc) =>
            Save(svc, spec.Path, () => FlowSpecEmitter.ToFileSource(spec)));
    }

    private static void MapTestSets(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/test-sets");

        g.MapGet("/", (WorkspaceService svc) => Results.Ok(
            (IReadOnlyList<TestSetSummaryDto>)svc.Current.TestSets
                .Select(t => new TestSetSummaryDto(t.RelativePath, DisplayName(t), t.Id, t.Tests.Count, t.Tags))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()));

        g.MapGet("/{*path}", (string path, WorkspaceService svc) =>
        {
            if (svc.Current.FindByPath(path) is not TestSetFile set) return Results.NotFound();
            return Results.Ok(new TestSetDetailDto(
                Path: set.RelativePath,
                Name: DisplayName(set),
                Id: set.Id,
                Vars: set.Vars,
                OnFailure: set.OnFailure.ToWire(),
                Tests: TestingSpecMapper.ToDto(set.Tests),
                Tags: set.Tags,
                Body: set.Body,
                Source: svc.ReadSource(set.RelativePath)));
        });

        g.MapPut("/spec", (TestSetSpecDto spec, WorkspaceService svc) =>
            Save(svc, spec.Path, () => TestSetSpecEmitter.ToFileSource(spec)));
    }

    private static void MapRun(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tests/run", async (HttpContext ctx, RunTestRequestDto body, WorkspaceService svc) =>
        {
            var resp = ctx.Response;
            resp.Headers.ContentType = "text/event-stream";
            resp.Headers.CacheControl = "no-cache";
            resp.Headers["X-Accel-Buffering"] = "no";

            var ct = ctx.RequestAborted;
            var runner = new TestRunner(svc.Pipeline);

            try
            {
                var result = await runner.RunAsync(
                    body,
                    new TestRunner.Callbacks(
                        OnStart: (start, token) => WriteEventAsync(resp, "start", start, token),
                        OnStep: (step, token) => WriteEventAsync(resp, "step", step, token),
                        OnEntry: (entry, token) => WriteEventAsync(resp, "entry", entry, token)),
                    ct).ConfigureAwait(false);

                await WriteEventAsync(resp, "done", result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client navigated away or hit Stop. Nothing to report to a socket that's gone.
            }
            catch (Exception ex)
            {
                // A run that can't start at all (missing file, unresolvable ref) still ends with
                // a `done` carrying the reason — the UI has exactly one place to stop spinning.
                var message = ex switch
                {
                    WorkspaceParseException parse => parse.Error.Message,
                    FileNotFoundException => ex.Message,
                    _ => ex.Message,
                };
                await WriteEventAsync(resp, "error", new ExecuteStreamErrorDto(message), ct).ConfigureAwait(false);
                await WriteEventAsync(resp, "done",
                    new TestRunResultDto(body.Path, "test", Stem(body.Path), [], false, 0, 0, 0, 0, message), ct)
                    .ConfigureAwait(false);
            }
        });
    }

    private static IResult Save(WorkspaceService svc, string path, Func<string> emit)
    {
        try
        {
            svc.Save(path, emit());
            // A create-then-open client refetches the moment this returns; the watcher's
            // debounced reload would lose that race and the GET would 404.
            svc.ReloadNow();
            return Results.NoContent();
        }
        catch (WorkspaceParseException ex)
        {
            return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
        }
    }

    private static string DisplayName(WorkspaceFile file)
        => string.IsNullOrWhiteSpace(file.Name) ? Stem(file.RelativePath) : file.Name!;

    /// <summary>Filename stem without the two-part suffix — <c>checkout.flow.tap</c> → <c>checkout</c>.</summary>
    private static string Stem(string relativePath)
    {
        var file = Path.GetFileName(relativePath);
        var cut = file.IndexOf('.');
        return cut <= 0 ? file : file[..cut];
    }

    private static async ValueTask WriteEventAsync<T>(HttpResponse resp, string name, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, typeof(T), StudioJson.Default);
        var sb = new StringBuilder(json.Length + 32);
        sb.Append("event: ").Append(name).Append('\n');
        sb.Append("data: ").Append(json).Append("\n\n");
        await resp.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
        await resp.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
