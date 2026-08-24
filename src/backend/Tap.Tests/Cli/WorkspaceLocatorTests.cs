using Tap.Studio.Cli.Workspace;

namespace Tap.Tests.Cli;

/// <summary>
/// Finding the workspace without being told where it is — what makes <c>tap-studio test</c>
/// work from anywhere inside a checkout, the way git does. Order of precedence: the
/// explicit flag, then the upward walk, then the downward fallback — which must be
/// deterministic (shallowest, then ordinal-first), bounded, and blind to dependency
/// folders.
/// </summary>
public class WorkspaceLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-cli-locate").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Manifest(string directory, string fileName = "workspace.tap")
        => File.WriteAllText(Path.Combine(directory, fileName), "---\nkind: workspace\nname: t\n---\n");

    [Fact]
    public void Finds_a_workspace_that_still_uses_the_legacy_manifest_name()
    {
        // Dual-read: a workspace that hasn't run `tap-studio migrate` must still be locatable,
        // or the migration command itself would be unreachable from inside it.
        var workspace = Dir("legacy-ws");
        Manifest(workspace, "tap.md");

        Assert.True(WorkspaceLocator.TryLocate(null, workspace, out var found, out var error), error);
        Assert.Equal(workspace, found);
    }

    [Fact]
    public void Finds_a_manifest_in_the_starting_directory()
    {
        var workspace = Dir("ws");
        Manifest(workspace);

        Assert.True(WorkspaceLocator.TryLocate(null, workspace, out var found, out var error), error);
        Assert.Equal(workspace, found);
    }

    [Fact]
    public void Walks_up_from_a_nested_directory()
    {
        var workspace = Dir("ws");
        Manifest(workspace);
        var deep = Dir("ws", "collections", "demo", "sub");

        Assert.True(WorkspaceLocator.TryLocate(null, deep, out var found, out var error), error);
        Assert.Equal(workspace, found);
    }

    [Fact]
    public void Finds_the_nested_tap_folder_layout()
    {
        // The older layout keeps workspace files in a `.tap/` subfolder of the repo.
        var repo = Dir("repo");
        var tap = Dir("repo", ".tap");
        Manifest(tap);

        Assert.True(WorkspaceLocator.TryLocate(null, repo, out var found, out var error), error);
        Assert.Equal(tap, found);
    }

    [Fact]
    public void The_nearest_workspace_wins()
    {
        var outer = Dir("outer");
        Manifest(outer);
        var inner = Dir("outer", "nested", "inner");
        Manifest(inner);

        Assert.True(WorkspaceLocator.TryLocate(null, inner, out var found, out _));
        Assert.Equal(inner, found);
    }

    [Fact]
    public void An_explicit_directory_is_used_as_given()
    {
        var workspace = Dir("ws");
        Manifest(workspace);
        var elsewhere = Dir("elsewhere");

        Assert.True(WorkspaceLocator.TryLocate(workspace, elsewhere, out var found, out _));
        Assert.Equal(workspace, found);
    }

    [Fact]
    public void An_explicit_directory_without_a_manifest_is_an_error()
    {
        // Deliberately does NOT fall back to walking up: someone who named a directory meant
        // that directory, and silently testing a different workspace is worse than failing.
        var empty = Dir("empty");
        Assert.False(WorkspaceLocator.TryLocate(empty, null, out _, out var error));
        Assert.Contains("does not contain a workspace.tap", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_directory_that_does_not_exist_says_so()
    {
        Assert.False(WorkspaceLocator.TryLocate(Path.Combine(_root, "ghost"), null, out _, out var error));
        Assert.Contains("is not a directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void No_workspace_anywhere_explains_both_ways_to_fix_it()
    {
        var orphan = Dir("orphan");
        // A temp directory has no manifest above it, up to the filesystem root.
        if (WorkspaceLocator.TryLocate(null, orphan, out _, out _)) return; // a real one exists above temp; skip

        Assert.False(WorkspaceLocator.TryLocate(null, orphan, out _, out var error));
        Assert.Contains("--workspace", error, StringComparison.Ordinal);
        Assert.Contains("workspace.tap", error, StringComparison.Ordinal);
    }

    // ---- The downward fallback -----------------------------------------------------------

    [Fact]
    public void A_workspace_beneath_the_start_directory_is_found()
    {
        var expected = Dir("samples", "sample-workspace");
        Manifest(expected);

        Assert.True(WorkspaceLocator.TryLocate(null, _root, out var found, out var error), error);
        Assert.Equal(expected, found);
    }

    [Fact]
    public void An_ancestor_workspace_still_wins_over_a_descendant()
    {
        Manifest(_root);
        Manifest(Dir("nested", "deeper"));
        var start = Dir("somewhere");

        Assert.True(WorkspaceLocator.TryLocate(null, start, out var found, out _));
        Assert.Equal(_root, found);
    }

    [Fact]
    public void The_first_workspace_is_the_shallowest_then_ordinal_first()
    {
        Manifest(Dir("z-shallow"));
        Manifest(Dir("a", "deeper"));

        // Depth 1 beats the ordinal-earlier depth 2.
        Assert.True(WorkspaceLocator.TryLocate(null, _root, out var found, out _));
        Assert.Equal(Path.Combine(_root, "z-shallow"), found);

        // Same depth as z-shallow, ordinal-earlier — now wins.
        Manifest(Dir("b-shallow"));
        Assert.True(WorkspaceLocator.TryLocate(null, _root, out found, out _));
        Assert.Equal(Path.Combine(_root, "b-shallow"), found);
    }

    [Fact]
    public void Dependency_and_dot_directories_are_never_searched()
    {
        Manifest(Dir("node_modules", "some-pkg"));
        Manifest(Dir(".hidden", "ws"));

        Assert.False(WorkspaceLocator.TryLocate(null, _root, out _, out var error));
        Assert.Contains("--workspace", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scan_depth_is_bounded()
    {
        Manifest(Dir("a", "b", "c", "d", "e", "f")); // depth 6 — one past the cap
        Assert.False(WorkspaceLocator.TryLocate(null, _root, out _, out _));

        var inReach = Dir("a", "b", "c", "d", "e"); // depth 5 — at the cap
        Manifest(inReach);
        Assert.True(WorkspaceLocator.TryLocate(null, _root, out var found, out _));
        Assert.Equal(inReach, found);
    }

    [Fact]
    public void The_dot_tap_layout_is_found_beneath_too()
    {
        var tap = Dir("svc", ".tap");
        Manifest(tap);

        Assert.True(WorkspaceLocator.TryLocate(null, _root, out var found, out _));
        Assert.Equal(tap, found);
    }
}
