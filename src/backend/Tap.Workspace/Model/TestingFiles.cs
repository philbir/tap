namespace Tap.Workspace.Model;

/// <summary>
/// A <c>*.flow.md</c> file: an ordered sequence of requests where each step can bind values out
/// of its response for the steps that follow. See §10 of <c>docs/workspace-format.md</c>.
///
/// <para>A flow composes existing requests — it never inlines one. That keeps a request the
/// single definition of what goes on the wire, so a flow step and a manual Send of the same
/// request produce the same exchange.</para>
/// </summary>
public sealed record FlowFile : WorkspaceFile
{
    /// <summary>Flow-scoped variables. Sit in the run's override tier, above every file scope
    /// and below the values a step binds. See §10.5.</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    public IReadOnlyList<FlowStep> Steps { get; init; } = [];
}

/// <summary>One step of a <see cref="FlowFile"/>.</summary>
public sealed record FlowStep
{
    /// <summary>The request this step sends. Resolved relative to the flow file, like every
    /// other <see cref="WorkspaceRef"/>.</summary>
    public required WorkspaceRef Request { get; init; }

    /// <summary>Display label. Null falls back to the referenced request's name.</summary>
    public string? Name { get; init; }

    /// <summary>Per-step variable overrides. Values are <em>templates</em>: they are expanded
    /// against the run bag before the request renders, which is what lets a step read an
    /// earlier step's output (<c>id: '{{orderId}}'</c>).</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Values bound out of this step's response for the steps after it.</summary>
    public IReadOnlyList<ExtractSpec> Extract { get; init; } = [];

    /// <summary>Assertions checked against this step's response, on top of whatever the
    /// referenced request file declares.</summary>
    public IReadOnlyList<AssertSpec> Assertions { get; init; } = [];

    /// <summary>Keep running the flow even when this step fails. Default false — the steps
    /// after a failed one would otherwise run against a state that never happened.</summary>
    public bool ContinueOnFailure { get; init; }

    /// <summary>Listed but not run; binds nothing.</summary>
    public bool Skip { get; init; }
}

/// <summary>What a run does when one of its entries fails.</summary>
public enum TestFailureMode
{
    /// <summary>Run every remaining test. The default — a test set is normally a group of
    /// independent checks, and knowing that three of them fail beats knowing that one does.</summary>
    Continue,

    /// <summary>Stop at the first failure. For a set whose entries build on each other.</summary>
    Stop,
}

/// <summary>
/// A <c>*.test.md</c> file: set-scoped variables plus a list of checks, each running one request
/// or one flow. See §11 of <c>docs/workspace-format.md</c>.
/// </summary>
public sealed record TestSetFile : WorkspaceFile
{
    /// <summary>Set-scoped variables — the "overwrite at last" tier, above every file scope.</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    public TestFailureMode OnFailure { get; init; } = TestFailureMode.Continue;

    public IReadOnlyList<TestEntry> Tests { get; init; } = [];
}

/// <summary>One check inside a <see cref="TestSetFile"/>. Exactly one of <see cref="Request"/>
/// and <see cref="Flow"/> is set — the parser rejects zero or both.</summary>
public sealed record TestEntry
{
    public string? Name { get; init; }

    /// <summary>The request this test sends, when it targets a single request.</summary>
    public WorkspaceRef? Request { get; init; }

    /// <summary>The flow this test runs, when it targets a sequence.</summary>
    public WorkspaceRef? Flow { get; init; }

    /// <summary>Per-test variable overrides. Templates, expanded against the run bag first.</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Assertions on top of the target's own. For a request entry they check its
    /// response; for a flow entry, the last step's — the response a caller of the flow sees.</summary>
    public IReadOnlyList<AssertSpec> Assertions { get; init; } = [];

    public bool Skip { get; init; }

    /// <summary>Wire form of what this entry targets: <c>request</c> or <c>flow</c>.</summary>
    public string TargetKind => Flow is not null ? "flow" : "request";

    /// <summary>The ref this entry targets, whichever kind it is.</summary>
    public WorkspaceRef? Target => Flow ?? Request;
}

/// <summary>Wire names for <see cref="TestFailureMode"/>.</summary>
public static class TestFailureModeWire
{
    public static string ToWire(this TestFailureMode mode)
        => mode == TestFailureMode.Stop ? "stop" : "continue";

    public static TestFailureMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "continue" => TestFailureMode.Continue,
        "stop" => TestFailureMode.Stop,
        _ => null,
    };
}
