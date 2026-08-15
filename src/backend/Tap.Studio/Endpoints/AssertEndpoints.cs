using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Execution.Contracts;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>POST /api/assertions/evaluate</c> — check a set of assertions against a response the
/// client already holds, without sending anything.
///
/// <para>This is what makes authoring assertions feel like editing rather than guessing:
/// Send once, then shape the assertions against the real response and watch each row flip
/// as you type. Evaluating on the server (rather than reimplementing the matchers in
/// TypeScript) is what guarantees the verdict you tune against is the verdict a later Send —
/// or a headless run — will produce.</para>
///
/// <para>Expected values are expanded through the same variable cascade as a real execution,
/// so <c>{{user.email}}</c> resolves identically here and on the wire.</para>
/// </summary>
public static class AssertEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/assertions/evaluate", async (
            EvaluateAssertsRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            try
            {
                var converted = AssertSpecMapper.ToModelTolerant(body.Assertions);
                if (converted.Count == 0)
                {
                    return Results.Ok(new EvaluateAssertsResponseDto([], new AssertSummaryDto(true, 0, 0, 0)));
                }

                // Only the entries that converted go to the renderer; the rest keep their
                // conversion error and are spliced back at their original index so the
                // client can line results up with its rows one-to-one.
                var renderable = converted.Where(c => c.Error is null).Select(c => c.Spec).ToArray();
                var rendered = await svc
                    .RenderAssertionsAsync(renderable, body.Path, body.Env, body.Stage, ct, tolerant: true)
                    .ConfigureAwait(false);

                var resolved = new List<ResolvedAssert>(converted.Count);
                var next = 0;
                foreach (var (spec, error) in converted)
                {
                    resolved.Add(error is null ? rendered[next++] : new ResolvedAssert(spec, false, error));
                }

                var snapshot = new ResponseSnapshot
                {
                    Status = body.Response.Status,
                    Headers = (body.Response.Headers ?? [])
                        .Select(h => new KeyValuePair<string, string>(h.Name, h.Value))
                        .ToArray(),
                    BodyText = body.Response.Body,
                    BodyTruncated = body.Response.BodyTruncated,
                    DurationMs = body.Response.DurationMs,
                };

                var results = AssertEvaluator.Evaluate(resolved, snapshot);
                return Results.Ok(new EvaluateAssertsResponseDto(
                    AssertSpecMapper.ToDto(results),
                    AssertSpecMapper.ToDto(AssertSummary.From(results))));
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
        });
    }
}
