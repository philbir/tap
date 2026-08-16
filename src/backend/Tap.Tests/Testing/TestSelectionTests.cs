using Microsoft.Extensions.Logging.Abstractions;
using Tap.Execution.Contracts;
using Tap.Execution.Testing;
using Tap.Execution.Variables;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Tests.Testing;

/// <summary>
/// Which entries a run covers, before anything is sent.
///
/// <para>Exercised through the real <see cref="TestRunner"/> rather than a private helper: the
/// plan arrives on the <c>OnStart</c> callback, which fires before the first request, so the
/// selection can be asserted without a server. Entries that survive selection then fail to
/// resolve — that is fine and deliberate, since what's under test is the plan.</para>
/// </summary>
public class TestSelectionTests
{
    private sealed class StubHost(LoadedWorkspace workspace) : IWorkspaceHost
    {
        public LoadedWorkspace Workspace { get; } = workspace;
        public string RootDirectory => "/ws";
        public IAuthTokenSource Tokens => NoAuthTokenSource.Instance;

        public VariableProviderRegistry CreateRegistry(EnvFile? env)
            => new ProviderRegistryBuilder([], NullLogger<ProviderRegistryBuilder>.Instance)
                .Build(Workspace, RootDirectory, [], null, env);
    }

    private static TestEntry Entry(string name) => new()
    {
        Name = name,
        // Points at nothing on purpose — selection happens before resolution.
        Request = WorkspaceRef.FromPath("./missing.req.tap"),
    };

    private static LoadedWorkspace WorkspaceWith(params string[] testNames)
    {
        var set = new TestSetFile
        {
            Kind = WorkspaceKind.Test,
            RelativePath = "tests/orders.test.tap",
            Name = "Order API",
            Tests = [.. testNames.Select(Entry)],
        };
        return new LoadedWorkspace("/ws", "/ws", [set], []);
    }

    /// <summary>Runs far enough to capture the plan, then reports what was selected.</summary>
    private static async Task<(IReadOnlyList<string> Planned, Exception? Failure)> PlanAsync(
        LoadedWorkspace workspace, RunTestRequestDto request)
    {
        var planned = new List<string>();
        var runner = new TestRunner(new RequestPipeline(new StubHost(workspace)));
        try
        {
            await runner.RunAsync(request, new TestRunner.Callbacks(
                OnStart: (start, _) => { planned.AddRange(start.Entries.Select(e => e.Name)); return ValueTask.CompletedTask; },
                OnStep: (_, _) => ValueTask.CompletedTask,
                OnEntry: (_, _) => ValueTask.CompletedTask),
                CancellationToken.None);
            return (planned, null);
        }
        catch (Exception ex)
        {
            return (planned, ex);
        }
    }

    private static RunTestRequestDto Request(int? only = null, string? filter = null, string path = "tests/orders.test.tap")
        => new(path, Env: null, Stage: null, Only: only, Overrides: null, FailFast: false, Filter: filter);

    [Fact]
    public async Task Every_test_runs_when_nothing_narrows_it()
    {
        var (planned, failure) = await PlanAsync(WorkspaceWith("alpha", "beta", "gamma"), Request());
        Assert.Null(failure);
        Assert.Equal(["alpha", "beta", "gamma"], planned);
    }

    [Fact]
    public async Task Filter_keeps_the_tests_whose_name_contains_it()
    {
        var (planned, failure) = await PlanAsync(
            WorkspaceWith("creates an order", "reads an order", "deletes a customer"),
            Request(filter: "order"));

        Assert.Null(failure);
        Assert.Equal(["creates an order", "reads an order"], planned);
    }

    [Fact]
    public async Task Filter_is_case_insensitive()
    {
        var (planned, _) = await PlanAsync(WorkspaceWith("Creates An Order"), Request(filter: "order"));
        Assert.Single(planned);
    }

    [Fact]
    public async Task Filter_and_only_narrow_together()
    {
        var (planned, _) = await PlanAsync(
            WorkspaceWith("order one", "order two", "customer"),
            Request(only: 1, filter: "order"));

        Assert.Equal(["order two"], planned);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_fails_rather_than_running_an_empty_set()
    {
        // The whole point: a misspelled filter must not produce a green run over zero tests.
        var (planned, failure) = await PlanAsync(WorkspaceWith("alpha", "beta"), Request(filter: "zzz"));

        Assert.Empty(planned);
        Assert.IsType<ArgumentException>(failure);
        Assert.Contains("matched none of the 2 tests", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_out_of_range_only_says_what_the_range_is()
    {
        var (_, failure) = await PlanAsync(WorkspaceWith("alpha", "beta"), Request(only: 7));
        Assert.IsType<ArgumentOutOfRangeException>(failure);
        Assert.Contains("has 2 tests (0…1)", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_negative_only_is_rejected_too()
    {
        var (_, failure) = await PlanAsync(WorkspaceWith("alpha"), Request(only: -1));
        Assert.IsType<ArgumentOutOfRangeException>(failure);
    }

    [Fact]
    public async Task An_empty_set_reports_nothing_ran_rather_than_erroring()
    {
        // An unfinished file is legitimate; only a *selection* that matches nothing is a
        // mistake worth failing over.
        var (planned, failure) = await PlanAsync(WorkspaceWith(), Request());
        Assert.Null(failure);
        Assert.Empty(planned);
    }

    [Fact]
    public async Task Filtering_a_flow_is_refused_because_its_steps_are_a_chain()
    {
        var flow = new FlowFile
        {
            Kind = WorkspaceKind.Flow,
            RelativePath = "tests/checkout.flow.tap",
            Name = "Checkout",
            Steps = [new FlowStep { Request = WorkspaceRef.FromPath("./missing.req.tap") }],
        };
        var workspace = new LoadedWorkspace("/ws", "/ws", [flow], []);

        var (_, failure) = await PlanAsync(workspace, Request(filter: "anything", path: "tests/checkout.flow.tap"));
        Assert.IsType<ArgumentException>(failure);
        Assert.Contains("run as one sequence", failure!.Message, StringComparison.Ordinal);
    }
}
