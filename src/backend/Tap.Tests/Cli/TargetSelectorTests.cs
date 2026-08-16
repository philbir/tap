using Tap.Execution.Agent;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Tests.Cli;

/// <summary>
/// Selecting by tag. The zero-match case carries the weight here: a misspelled tag that
/// selected nothing and exited 0 would leave a pipeline green while testing nothing, with no
/// signal anywhere to notice it by.
/// </summary>
public class TargetSelectorTests
{
    private static LoadedWorkspace Workspace(params WorkspaceFile[] files) => new("/ws", "/ws", files, []);

    private static TestSetFile Set(string path, string name, params string[] tags) => new()
    {
        Kind = WorkspaceKind.Test,
        RelativePath = path,
        Name = name,
        Tags = tags,
        Tests = [new TestEntry { Request = WorkspaceRef.FromPath("./a.req.tap") }],
    };

    private static FlowFile Flow(string path, string name, params string[] tags) => new()
    {
        Kind = WorkspaceKind.Flow,
        RelativePath = path,
        Name = name,
        Tags = tags,
    };

    private static RequestFile Request(string path, params string[] tags) => new()
    {
        Kind = WorkspaceKind.Request,
        RelativePath = path,
        Tags = tags,
    };

    [Fact]
    public void Selects_every_file_carrying_the_tag()
    {
        var ws = Workspace(
            Set("tests/a.test.tap", "A", "smoke"),
            Set("tests/b.test.tap", "B", "nightly"),
            Flow("tests/c.flow.tap", "C", "smoke"));

        Assert.True(TargetSelector.TryByTags(ws, ["smoke"], out var targets, out var error), error);
        Assert.Equal(["tests/a.test.tap", "tests/c.flow.tap"], targets.Select(t => t.Path));
    }

    [Fact]
    public void Repeated_tags_union_rather_than_intersect()
    {
        // "Run the smoke tests and the graphql tests" is what the flag reads like. It is also
        // the safer default: intersecting would silently select fewer, sometimes none.
        var ws = Workspace(
            Set("tests/a.test.tap", "A", "smoke"),
            Set("tests/b.test.tap", "B", "graphql"),
            Set("tests/c.test.tap", "C", "nightly"));

        Assert.True(TargetSelector.TryByTags(ws, ["smoke", "graphql"], out var targets, out _));
        Assert.Equal(["tests/a.test.tap", "tests/b.test.tap"], targets.Select(t => t.Path));
    }

    [Fact]
    public void A_file_carrying_both_tags_is_selected_once()
    {
        var ws = Workspace(Set("tests/a.test.tap", "A", "smoke", "graphql"));
        Assert.True(TargetSelector.TryByTags(ws, ["smoke", "graphql"], out var targets, out _));
        Assert.Single(targets);
    }

    [Fact]
    public void Tags_match_case_insensitively()
    {
        var ws = Workspace(Set("tests/a.test.tap", "A", "Smoke"));
        Assert.True(TargetSelector.TryByTags(ws, ["smoke"], out var targets, out _));
        Assert.Single(targets);
    }

    [Fact]
    public void Requests_are_never_selected_even_when_they_carry_the_tag()
    {
        // A tagged request is not a runnable target for `test`; picking it up would run one
        // request and call it a test set.
        var ws = Workspace(Set("tests/a.test.tap", "A", "smoke"), Request("collections/demo/x.req.tap", "smoke"));
        Assert.True(TargetSelector.TryByTags(ws, ["smoke"], out var targets, out _));
        Assert.Single(targets);
        Assert.Equal("tests/a.test.tap", targets[0].Path);
    }

    [Fact]
    public void Order_is_stable_across_runs()
    {
        var ws = Workspace(
            Set("tests/z.test.tap", "Z", "smoke"),
            Set("tests/a.test.tap", "A", "smoke"),
            Set("tests/m.test.tap", "M", "smoke"));

        Assert.True(TargetSelector.TryByTags(ws, ["smoke"], out var targets, out _));
        Assert.Equal(["tests/a.test.tap", "tests/m.test.tap", "tests/z.test.tap"], targets.Select(t => t.Path));
    }

    [Fact]
    public void A_tag_that_matches_nothing_is_an_error_not_an_empty_run()
    {
        var ws = Workspace(Set("tests/a.test.tap", "A", "smoke"));
        Assert.False(TargetSelector.TryByTags(ws, ["nope"], out var targets, out var error));
        Assert.Empty(targets);
        Assert.Contains("No test sets or flows are tagged 'nope'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmatched_tag_lists_the_tags_that_do_exist()
    {
        var ws = Workspace(Set("tests/a.test.tap", "A", "smoke"), Flow("tests/b.flow.tap", "B", "graphql"));
        Assert.False(TargetSelector.TryByTags(ws, ["nope"], out _, out var error));
        Assert.Contains("Tags in use: graphql, smoke", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workspace_with_no_tags_says_so()
    {
        var ws = Workspace(Set("tests/a.test.tap", "A"));
        Assert.False(TargetSelector.TryByTags(ws, ["smoke"], out _, out var error));
        Assert.Contains("carries any tag", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_tag_list_is_a_usage_error()
    {
        var ws = Workspace(Set("tests/a.test.tap", "A", "smoke"));
        Assert.False(TargetSelector.TryByTags(ws, ["  "], out _, out var error));
        Assert.Contains("--tag needs a tag name", error, StringComparison.Ordinal);
    }
}
