using System.Diagnostics;
using Tap.Execution.Contracts;
using Tap.Execution.Asserts;
using Tap.Execution.Http;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Testing;

namespace Tap.Execution.Testing;

/// <summary>
/// Runs a test set or a flow. The composition layer over everything that already exists: each
/// step renders and executes through the same path a manual Send takes
/// (<see cref="RequestPipeline.RenderAsync"/> + <see cref="HttpExecutionHelpers"/>), gets its
/// assertions evaluated by the same <see cref="AssertEvaluator"/>, and hands its response to
/// <see cref="ValueExtractor"/> for the values the next step reads.
///
/// <para>Nothing here reimplements request behaviour. A step and a Send of the same request
/// produce the same exchange — same auth injection, same redirect policy, same body cap — which
/// is the whole reason a test result is worth anything.</para>
///
/// <para>Progress is reported through callbacks as it happens rather than returned at the end:
/// a ten-step flow against a slow API is the normal case, and a spinner that sits still for
/// thirty seconds tells the user nothing.</para>
/// </summary>
public sealed class TestRunner(RequestPipeline pipeline)
{
    /// <summary>Cap on the response body carried in a step result. Enough to diagnose a failed
    /// assertion; far short of shipping 2 MiB per step down the event stream.</summary>
    public const int BodyPreviewCap = 64 * 1024;

    /// <summary>Ceiling on how many requests one run may fire. A flow can reference a flow —
    /// not through the file format, but a test set can name a flow and a future edit could make
    /// that recursive — and a runaway run is a lot of real traffic against someone's API.</summary>
    private const int MaxStepsPerRun = 500;

    public sealed record Callbacks(
        Func<TestRunStartDto, CancellationToken, ValueTask> OnStart,
        Func<TestRunStepEventDto, CancellationToken, ValueTask> OnStep,
        Func<TestEntryResultDto, CancellationToken, ValueTask> OnEntry);

    public async Task<TestRunResultDto> RunAsync(
        RunTestRequestDto request, Callbacks callbacks, CancellationToken ct)
    {
        var ws = pipeline.Workspace;
        var target = ws.FindByPath(request.Path)
            ?? throw new FileNotFoundException($"'{request.Path}' is not in the workspace.");

        return target switch
        {
            TestSetFile set => await RunTestSetAsync(ws, set, request, callbacks, ct).ConfigureAwait(false),
            FlowFile flow => await RunStandaloneFlowAsync(ws, flow, request, callbacks, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"'{request.Path}' is not a test set or a flow."),
        };
    }

    /// <summary>
    /// Runs a single request and returns its step result — the same shape a test's step has,
    /// because it is the same code. Backs <c>tap-studio send</c>.
    ///
    /// <para>Deliberately not a separate execution path: a request sent on its own and the same
    /// request sent inside a test set must produce the same exchange and the same verdicts, and
    /// the only durable way to guarantee that is to have one implementation.</para>
    /// </summary>
    public async Task<TestStepResultDto> SendAsync(RunTestRequestDto request, CancellationToken ct)
    {
        var ws = pipeline.Workspace;
        var requestFile = ws.FindByPath(request.Path) as RequestFile
            ?? throw new FileNotFoundException($"'{request.Path}' is not a request in this workspace.");

        var name = requestFile.Name ?? Stem(requestFile.RelativePath);
        var bag = RunVariables.Literals(new Dictionary<string, VarSpec>(), request.Overrides);

        var outcome = await RunStepAsync(
            requestFile, stepIndex: 0, name, bag, templateVars: null,
            extra: [], extract: [], request, new StepBudget(), ct).ConfigureAwait(false);
        return outcome.Result;
    }

    // -------------------------------------------------------------------------------------
    // Test sets
    // -------------------------------------------------------------------------------------

    private async Task<TestRunResultDto> RunTestSetAsync(
        LoadedWorkspace ws, TestSetFile set, RunTestRequestDto request, Callbacks callbacks, CancellationToken ct)
    {
        var name = set.Name ?? Stem(set.RelativePath);
        var selected = Select(ws, set, request);

        await callbacks.OnStart(new TestRunStartDto(
            set.RelativePath, "test", name, request.Env, request.Stage,
            selected.Select(s => new TestRunPlanEntryDto(
                s.Index,
                EntryName(ws, set, s.Value),
                s.Value.TargetKind,
                s.Value.Target?.SourceText,
                s.Value.Skip)).ToArray()), ct).ConfigureAwait(false);

        // The set's own variables sit under everything a run produces — the "overwrite at last"
        // tier relative to the workspace files, but still below anything a flow binds.
        var baseBag = RunVariables.Literals(set.Vars, request.Overrides);

        var clock = Stopwatch.StartNew();
        var entries = new List<TestEntryResultDto>(selected.Count);
        var budget = new StepBudget();

        foreach (var (index, entry) in selected)
        {
            var result = await RunEntryAsync(ws, set, entry, index, baseBag, request, callbacks, budget, ct)
                .ConfigureAwait(false);
            entries.Add(result);
            await callbacks.OnEntry(result, ct).ConfigureAwait(false);

            var onFailure = request.FailFast ? TestFailureMode.Stop : set.OnFailure;
            if (!result.Ok && !result.Skipped && onFailure == TestFailureMode.Stop) break;
        }

        clock.Stop();
        return Summarize(set.RelativePath, "test", name, entries, clock.Elapsed.TotalMilliseconds);
    }

    private async Task<TestEntryResultDto> RunEntryAsync(
        LoadedWorkspace ws,
        TestSetFile set,
        TestEntry entry,
        int index,
        IReadOnlyDictionary<string, string> baseBag,
        RunTestRequestDto request,
        Callbacks callbacks,
        StepBudget budget,
        CancellationToken ct)
    {
        var name = EntryName(ws, set, entry);
        var targetPath = entry.Target?.SourceText;

        if (entry.Skip)
            return new TestEntryResultDto(index, name, entry.TargetKind, targetPath, [], true, true, 0, null);

        var setDir = DirectoryOf(set.RelativePath);
        var clock = Stopwatch.StartNew();

        try
        {
            // Entry variables join the bag before the target runs, so every step of a flow entry
            // sees them. They are templates, expanded against the bag as it stands.
            var bag = new Dictionary<string, string>(baseBag, StringComparer.Ordinal);

            if (entry.Flow is not null)
            {
                var flow = ws.Resolve(entry.Flow, setDir) as FlowFile
                    ?? throw new InvalidOperationException(
                        $"'{entry.Flow.SourceText}' does not resolve to a flow in this workspace.");

                RunVariables.MergeLiterals(bag, flow.Vars);
                var steps = await RunFlowStepsAsync(
                    ws, flow, bag, entry.Vars, entry.Assertions, request, index, callbacks, budget, ct)
                    .ConfigureAwait(false);
                clock.Stop();
                return EntryFrom(index, name, "flow", targetPath, steps, clock.Elapsed.TotalMilliseconds);
            }

            var requestFile = ws.Resolve(entry.Request, setDir) as RequestFile
                ?? throw new InvalidOperationException(
                    $"'{entry.Request!.SourceText}' does not resolve to a request in this workspace.");

            var step = await RunStepAsync(
                requestFile, stepIndex: 0, name, bag, RunVariables.Templates(entry.Vars, null),
                extra: entry.Assertions, extract: [], request, budget, ct).ConfigureAwait(false);
            await callbacks.OnStep(new TestRunStepEventDto(index, step.Result), ct).ConfigureAwait(false);

            clock.Stop();
            return EntryFrom(index, name, "request", targetPath, [step.Result], clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clock.Stop();
            return new TestEntryResultDto(
                index, name, entry.TargetKind, targetPath, [], false, false,
                clock.Elapsed.TotalMilliseconds, Describe(ex));
        }
    }

    // -------------------------------------------------------------------------------------
    // Flows
    // -------------------------------------------------------------------------------------

    /// <summary>A flow run on its own is reported as a one-entry run, so the UI renders the same
    /// shape whether the flow was launched from its own editor or from inside a test set.</summary>
    private async Task<TestRunResultDto> RunStandaloneFlowAsync(
        LoadedWorkspace ws, FlowFile flow, RunTestRequestDto request, Callbacks callbacks, CancellationToken ct)
    {
        var name = flow.Name ?? Stem(flow.RelativePath);

        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            // Filtering a flow's steps would cut the chain a later step depends on.
            throw new ArgumentException(
                $"--filter narrows the tests inside a set; '{flow.RelativePath}' is a flow, "
                + "whose steps run as one sequence.",
                nameof(request));
        }

        await callbacks.OnStart(new TestRunStartDto(
            flow.RelativePath, "flow", name, request.Env, request.Stage,
            [new TestRunPlanEntryDto(0, name, "flow", flow.RelativePath, false)]), ct).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        var bag = RunVariables.Literals(flow.Vars, request.Overrides);
        TestEntryResultDto entry;

        try
        {
            var steps = await RunFlowStepsAsync(
                ws, flow, bag, entryVars: null, entryAssertions: [], request, 0, callbacks,
                new StepBudget(), ct).ConfigureAwait(false);
            entry = EntryFrom(0, name, "flow", flow.RelativePath, steps, clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entry = new TestEntryResultDto(
                0, name, "flow", flow.RelativePath, [], false, false, clock.Elapsed.TotalMilliseconds, Describe(ex));
        }

        await callbacks.OnEntry(entry, ct).ConfigureAwait(false);
        clock.Stop();
        return Summarize(flow.RelativePath, "flow", name, [entry], clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Runs a flow's steps in order, threading the variable bag through them. Each step's bound
    /// values enter the bag before the next step renders — that thread is the flow.
    /// </summary>
    /// <param name="entryVars">Templates from the test entry that named this flow, applied to
    /// every step on top of the bag.</param>
    /// <param name="entryAssertions">Assertions the test entry declared. They check the last
    /// step's response — the one a caller of the flow sees.</param>
    private async Task<List<TestStepResultDto>> RunFlowStepsAsync(
        LoadedWorkspace ws,
        FlowFile flow,
        Dictionary<string, string> bag,
        IReadOnlyDictionary<string, string>? entryVars,
        IReadOnlyList<AssertSpec> entryAssertions,
        RunTestRequestDto request,
        int entryIndex,
        Callbacks callbacks,
        StepBudget budget,
        CancellationToken ct)
    {
        var flowDir = DirectoryOf(flow.RelativePath);
        var results = new List<TestStepResultDto>(flow.Steps.Count);

        for (var i = 0; i < flow.Steps.Count; i++)
        {
            var step = flow.Steps[i];
            var last = i == flow.Steps.Count - 1;
            var name = step.Name ?? StepNameFrom(ws, flow, step, i);

            if (step.Skip)
            {
                var skipped = SkippedStep(i, name, step.Request.SourceText);
                results.Add(skipped);
                await callbacks.OnStep(new TestRunStepEventDto(entryIndex, skipped), ct).ConfigureAwait(false);
                continue;
            }

            var requestFile = ws.Resolve(step.Request, flowDir) as RequestFile
                ?? throw new InvalidOperationException(
                    $"Step {i + 1} ('{name}'): '{step.Request.SourceText}' does not resolve to a request in this workspace.");

            // Step vars win over the entry's — a single step pinning a value is the most
            // specific statement in the file.
            var templates = RunVariables.Templates(entryVars, step.Vars);

            // Only the last step answers to the entry's assertions.
            var extra = last && entryAssertions.Count > 0
                ? Concat(step.Assertions, entryAssertions)
                : step.Assertions;

            var outcome = await RunStepAsync(
                requestFile, i, name, bag, templates, extra, step.Extract, request, budget, ct).ConfigureAwait(false);

            results.Add(outcome.Result);
            await callbacks.OnStep(new TestRunStepEventDto(entryIndex, outcome.Result), ct).ConfigureAwait(false);

            foreach (var (key, value) in outcome.Bound) bag[key] = value;

            // A failed step invalidates everything after it: those steps were going to run
            // against a state that never happened.
            if (!outcome.Result.Ok && !step.ContinueOnFailure)
            {
                for (var rest = i + 1; rest < flow.Steps.Count; rest++)
                {
                    var notRun = NotRunStep(
                        rest,
                        flow.Steps[rest].Name ?? StepNameFrom(ws, flow, flow.Steps[rest], rest),
                        flow.Steps[rest].Request.SourceText,
                        $"Not run — step {i + 1} ('{name}') failed.");
                    results.Add(notRun);
                    await callbacks.OnStep(new TestRunStepEventDto(entryIndex, notRun), ct).ConfigureAwait(false);
                }
                break;
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------------------
    // One request
    // -------------------------------------------------------------------------------------

    private readonly record struct StepOutcome(
        TestStepResultDto Result, IReadOnlyList<KeyValuePair<string, string>> Bound);

    /// <summary>
    /// Renders and sends one request, then evaluates its assertions and runs its extractions.
    /// Mirrors <see cref="ExecuteEndpoint"/>'s non-streaming path deliberately: the point of a
    /// test is that it exercises what a Send exercises.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        RequestFile requestFile,
        int stepIndex,
        string name,
        IReadOnlyDictionary<string, string> bag,
        IReadOnlyList<KeyValuePair<string, string>>? templateVars,
        IReadOnlyList<AssertSpec> extra,
        IReadOnlyList<ExtractSpec> extract,
        RunTestRequestDto request,
        StepBudget budget,
        CancellationToken ct)
    {
        budget.Take(name);

        var templates = templateVars is { Count: > 0 } ? templateVars : null;
        var clock = Stopwatch.StartNew();
        ResolvedRequest? rendered = null;

        try
        {
            rendered = await pipeline.RenderAsync(
                requestFile, request.Env, bag, ct, request.Stage, templates).ConfigureAwait(false);

            HttpExecutionHelpers.ValidateScheme(rendered);
            rendered = HttpExecutionHelpers.WithDefaultUserAgent(rendered);

            if (rendered.Protocol == RequestProtocol.WebSocket)
            {
                clock.Stop();
                return new StepOutcome(
                    Failed(stepIndex, name, requestFile.RelativePath, rendered, clock.Elapsed.TotalMilliseconds,
                        "WebSocket requests can't be run inside a flow or test set yet — assertions on frames " +
                        "are not modelled. Send it from the request editor instead."),
                    []);
            }

            using var message = HttpExecutionHelpers.BuildRequest(rendered);
            using var timeout = HttpTransport.CreateTimeout(rendered, ct);
            using var client = HttpTransport.CreateClient(rendered);

            using var response = await HttpExecutionHelpers.SendFollowingRedirectsAsync(
                client, message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            var (bytes, total) = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            clock.Stop();

            var contentType = response.Content.Headers.ContentType?.ToString();
            var bodyText = HttpExecutionHelpers.TryDecodeBody(bytes, contentType, total);
            var headers = HttpExecutionHelpers.FlattenHeaders(response);
            var status = (int)response.StatusCode;
            var elapsed = clock.Elapsed.TotalMilliseconds;

            // The request file's own assertions were expanded by the render; the step's are
            // expanded here against the same bag, then both are evaluated as one list so the
            // result indexes stay contiguous.
            var assertions = rendered.Assertions;
            if (extra.Count > 0)
            {
                var resolvedExtra = await pipeline.RenderAssertionsAsync(
                    extra, requestFile.RelativePath, request.Env, request.Stage, ct,
                    tolerant: true, overrides: bag, templateOverrides: templates).ConfigureAwait(false);
                assertions = [.. assertions, .. resolvedExtra];
            }

            var (results, summary) = AssertRunner.Run(
                assertions, rendered.Protocol, status, headers, bodyText, total, elapsed);

            var snapshot = new ResponseSnapshot
            {
                Status = status,
                Headers = headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToArray(),
                BodyText = bodyText,
                BodyTruncated = total > HttpExecutionHelpers.BodyCap,
                DurationMs = elapsed,
            };

            var bound = ValueExtractor.Extract(extract, snapshot);
            var extracted = bound
                .Select(b => new ExtractedValueDto(b.Var, b.Value, b.Error))
                .ToArray();

            // A step passes when every assertion held and every extraction bound what it
            // promised. Extraction counts because the next step depends on it.
            var ok = (summary?.Ok ?? true) && bound.All(b => b.Ok);

            return new StepOutcome(
                new TestStepResultDto(
                    Index: stepIndex,
                    Name: name,
                    RequestPath: requestFile.RelativePath,
                    Method: rendered.Method,
                    Url: rendered.Url,
                    Status: status,
                    StatusText: response.ReasonPhrase,
                    ContentType: contentType,
                    ResponseBody: Preview(bodyText),
                    ResponseBodyBytes: total,
                    DurationMs: elapsed,
                    Assertions: results,
                    AssertSummary: summary,
                    Extracted: extracted,
                    Ok: ok,
                    Skipped: false,
                    Error: null),
                bound.Where(b => b.Bound)
                    .Select(b => new KeyValuePair<string, string>(b.Var, b.Value!))
                    .ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            clock.Stop();
            return new StepOutcome(
                Failed(stepIndex, name, requestFile.RelativePath, rendered, clock.Elapsed.TotalMilliseconds, Describe(ex)),
                []);
        }
    }

    private static async Task<(byte[] Bytes, long Total)> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (buffer.Length < HttpExecutionHelpers.BodyCap)
            {
                var slack = HttpExecutionHelpers.BodyCap - (int)buffer.Length;
                buffer.Write(chunk, 0, Math.Min(read, slack));
            }
        }
        return (buffer.ToArray(), total);
    }

    // -------------------------------------------------------------------------------------
    // Bag composition
    // -------------------------------------------------------------------------------------

    private static IReadOnlyList<AssertSpec> Concat(IReadOnlyList<AssertSpec> a, IReadOnlyList<AssertSpec> b)
        => a.Count == 0 ? b : b.Count == 0 ? a : [.. a, .. b];

    // -------------------------------------------------------------------------------------
    // Result shaping
    // -------------------------------------------------------------------------------------

    private static TestEntryResultDto EntryFrom(
        int index, string name, string targetKind, string? targetPath,
        IReadOnlyList<TestStepResultDto> steps, double durationMs)
    {
        var ok = steps.All(s => s.Ok || s.Skipped);
        var failed = steps.FirstOrDefault(s => !s.Ok && !s.Skipped);
        return new TestEntryResultDto(index, name, targetKind, targetPath, steps, ok, false, durationMs, WhyItFailed(failed));
    }

    /// <summary>
    /// One line naming why an entry failed, for a collapsed row that isn't showing its steps.
    /// A step carries its reason in one of three places depending on how it went wrong, and the
    /// row shouldn't just say "failed" while the detail sits two clicks away.
    /// </summary>
    private static string? WhyItFailed(TestStepResultDto? step)
    {
        if (step is null) return null;
        if (step.Error is { } error) return error;

        if (step.Assertions.FirstOrDefault(a => !a.Ok && !a.Skipped) is { } assertion)
        {
            return assertion.Message
                ?? $"{assertion.Name} — expected {assertion.Expected ?? "—"}, got {assertion.Actual ?? "nothing"}.";
        }

        return step.Extracted.FirstOrDefault(e => e.Error is not null)?.Error;
    }

    private static TestRunResultDto Summarize(
        string path, string kind, string name, IReadOnlyList<TestEntryResultDto> entries, double durationMs)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var entry in entries)
        {
            if (entry.Skipped) skipped++;
            else if (entry.Ok) passed++;
            else failed++;
        }
        return new TestRunResultDto(path, kind, name, entries, failed == 0, passed, failed, skipped, durationMs, null);
    }

    private static TestStepResultDto Failed(
        int index, string name, string? requestPath, ResolvedRequest? rendered, double durationMs, string error)
        => new(
            index, name, requestPath,
            rendered?.Method ?? "—", rendered?.Url ?? string.Empty,
            0, null, null, null, 0, durationMs,
            [], null, [], false, false, error);

    private static TestStepResultDto SkippedStep(int index, string name, string? requestPath)
        => new(index, name, requestPath, "—", string.Empty, 0, null, null, null, 0, 0,
            [], null, [], true, true, "Skipped.");

    private static TestStepResultDto NotRunStep(int index, string name, string? requestPath, string reason)
        => new(index, name, requestPath, "—", string.Empty, 0, null, null, null, 0, 0,
            [], null, [], true, true, reason);

    private static string? Preview(string? body)
    {
        if (body is null) return null;
        return body.Length <= BodyPreviewCap
            ? body
            : body[..BodyPreviewCap] + $"\n\n[…{body.Length - BodyPreviewCap:N0} more characters — open the request to see the whole response]";
    }

    // -------------------------------------------------------------------------------------
    // Naming and selection
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The entries this run will execute, after <c>--only</c> and <c>--filter</c>.
    ///
    /// <para>A selection that matches nothing throws rather than running an empty set. A run
    /// that quietly passes because a filter was misspelled is the worst outcome available
    /// here: it is green, it is wrong, and nothing about the output says so.</para>
    /// </summary>
    private static List<(int Index, TestEntry Value)> Select(
        LoadedWorkspace workspace, TestSetFile set, RunTestRequestDto request)
    {
        var tests = set.Tests;

        if (request.Only is { } index && (index < 0 || index >= tests.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"--only {index} is out of range: '{set.RelativePath}' has {tests.Count} "
                + $"{(tests.Count == 1 ? "test" : "tests")} (0…{Math.Max(0, tests.Count - 1)}).");
        }

        var filter = string.IsNullOrWhiteSpace(request.Filter) ? null : request.Filter!.Trim();
        var selected = new List<(int, TestEntry)>(tests.Count);
        for (var i = 0; i < tests.Count; i++)
        {
            if (request.Only is { } wanted && wanted != i) continue;
            if (filter is not null
                && !EntryName(workspace, set, tests[i]).Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            selected.Add((i, tests[i]));
        }

        // An empty *file* is a legitimate unfinished one and reports "nothing ran". An empty
        // *selection* means the caller asked for something that isn't there.
        if (selected.Count == 0 && tests.Count > 0 && filter is not null)
        {
            throw new ArgumentException(
                $"--filter '{filter}' matched none of the {tests.Count} tests in '{set.RelativePath}'.",
                nameof(request));
        }

        return selected;
    }

    /// <summary>An entry's label: its own <c>name:</c>, else the target file's display name,
    /// else the ref as written. Resolved server-side so a run report, the editor, and a future
    /// CLI all agree on what a row is called.</summary>
    private static string EntryName(LoadedWorkspace ws, TestSetFile set, TestEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name)) return entry.Name!;
        var resolved = ws.Resolve(entry.Target, DirectoryOf(set.RelativePath));
        if (resolved?.Name is { Length: > 0 } name) return name;
        return entry.Target?.SourceText ?? "(unnamed)";
    }

    private static string StepNameFrom(LoadedWorkspace ws, FlowFile flow, FlowStep step, int index)
    {
        var resolved = ws.Resolve(step.Request, DirectoryOf(flow.RelativePath));
        if (resolved?.Name is { Length: > 0 } name) return name;
        return $"Step {index + 1}";
    }

    private static string DirectoryOf(string relativePath) => Path.GetDirectoryName(relativePath) ?? string.Empty;

    private static string Stem(string relativePath)
    {
        var file = Path.GetFileName(relativePath);
        var cut = file.IndexOf('.');
        return cut <= 0 ? file : file[..cut];
    }

    private static string Describe(Exception ex) => ex switch
    {
        WorkspaceParseException parse => parse.Error.Message,
        HttpRequestException or TaskCanceledException => HttpTransport.DescribeException(ex),
        _ => ex.Message,
    };

    /// <summary>Bounds the total number of requests a single run may fire.</summary>
    private sealed class StepBudget
    {
        private int _spent;

        public void Take(string name)
        {
            if (++_spent > MaxStepsPerRun)
            {
                throw new InvalidOperationException(
                    $"This run reached the {MaxStepsPerRun}-request ceiling at '{name}'. " +
                    "Split it into smaller test sets.");
            }
        }
    }
}
