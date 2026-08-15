using Tap.Execution.Agent;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Tests.Cli;

/// <summary>
/// Turning what someone typed into the file they meant. The ambiguity rule is the one that
/// matters: a CLI that guesses between two same-named test sets produces a pipeline that
/// reports on something other than what its author believes.
/// </summary>
public class TargetResolverTests
{
    private static LoadedWorkspace Workspace(params WorkspaceFile[] files)
        => new("/ws", "/ws", files, []);

    private static TestSetFile Set(string path, string? name) => new()
    {
        Kind = WorkspaceKind.Test,
        RelativePath = path,
        Name = name,
        Tests = [new TestEntry { Request = WorkspaceRef.FromPath("./a.req.md") }],
    };

    private static FlowFile Flow(string path, string? name) => new()
    {
        Kind = WorkspaceKind.Flow,
        RelativePath = path,
        Name = name,
    };

    private static readonly WorkspaceKind[] Runnable = [WorkspaceKind.Test, WorkspaceKind.Flow];

    [Fact]
    public void Resolves_by_workspace_relative_path()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"));
        Assert.True(TargetResolver.TryResolve(ws, "tests/orders.test.md", Runnable, out var target, out _));
        Assert.Equal("tests/orders.test.md", target.Path);
    }

    [Fact]
    public void Resolves_by_display_name()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"));
        Assert.True(TargetResolver.TryResolve(ws, "Order API", Runnable, out var target, out _));
        Assert.Equal("tests/orders.test.md", target.Path);
    }

    [Fact]
    public void Resolves_by_filename_stem()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"));
        Assert.True(TargetResolver.TryResolve(ws, "orders", Runnable, out var target, out _));
        Assert.Equal("tests/orders.test.md", target.Path);
    }

    [Fact]
    public void Names_are_matched_case_insensitively()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"));
        Assert.True(TargetResolver.TryResolve(ws, "order api", Runnable, out _, out _));
    }

    [Fact]
    public void Backslashes_in_a_path_are_accepted()
    {
        // Someone on Windows tab-completing a path gets backslashes; the workspace index is
        // keyed on forward slashes.
        var ws = Workspace(Set("tests/orders.test.md", "Order API"));
        Assert.True(TargetResolver.TryResolve(ws, @"tests\orders.test.md", Runnable, out _, out _));
    }

    [Fact]
    public void Flows_resolve_alongside_test_sets()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"), Flow("tests/checkout.flow.md", "Checkout"));
        Assert.True(TargetResolver.TryResolve(ws, "Checkout", Runnable, out var target, out _));
        Assert.Equal("tests/checkout.flow.md", target.Path);
    }

    [Fact]
    public void A_kind_filter_excludes_everything_else()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"), Flow("tests/checkout.flow.md", "Checkout"));
        Assert.False(TargetResolver.TryResolve(ws, "Checkout", [WorkspaceKind.Test], out _, out var error));
        Assert.Contains("No test sets match", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_name_lists_the_candidates_rather_than_guessing()
    {
        var ws = Workspace(Set("tests/a.test.md", "Smoke"), Set("tests/b.test.md", "Smoke"));
        Assert.False(TargetResolver.TryResolve(ws, "Smoke", Runnable, out _, out var error));
        Assert.Contains("ambiguous", error, StringComparison.Ordinal);
        Assert.Contains("tests/a.test.md", error, StringComparison.Ordinal);
        Assert.Contains("tests/b.test.md", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_wins_over_an_ambiguous_name()
    {
        var ws = Workspace(Set("tests/a.test.md", "Smoke"), Set("tests/b.test.md", "Smoke"));
        Assert.True(TargetResolver.TryResolve(ws, "tests/a.test.md", Runnable, out var target, out _));
        Assert.Equal("tests/a.test.md", target.Path);
    }

    [Fact]
    public void A_near_miss_suggests_what_was_meant()
    {
        var ws = Workspace(Set("tests/orders.test.md", "Order API"));
        Assert.False(TargetResolver.TryResolve(ws, "Order", Runnable, out _, out var error));
        Assert.Contains("Did you mean", error, StringComparison.Ordinal);
        Assert.Contains("Order API", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_workspace_says_so()
    {
        Assert.False(TargetResolver.TryResolve(Workspace(), "anything", Runnable, out _, out var error));
        Assert.Contains("contains no", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests/orders.test.md", "orders")]
    [InlineData("tests/checkout.flow.md", "checkout")]
    [InlineData("collections/demo/get.req.md", "get")]
    [InlineData("tap.md", "tap")]
    public void Stem_drops_the_two_part_suffix(string path, string expected)
        => Assert.Equal(expected, TargetResolver.Stem(path));
}
