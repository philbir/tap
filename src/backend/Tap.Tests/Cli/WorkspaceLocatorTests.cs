using Tap.Studio.Cli.Workspace;

namespace Tap.Tests.Cli;

/// <summary>
/// Finding the workspace without being told where it is — what makes <c>tap-studio test</c>
/// work from anywhere inside a checkout, the way git does.
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

    private static void Manifest(string directory)
        => File.WriteAllText(Path.Combine(directory, "tap.md"), "---\nkind: workspace\nname: t\n---\n");

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
        Assert.Contains("does not contain a tap.md", error, StringComparison.Ordinal);
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
        // A temp directory has no tap.md above it, up to the filesystem root.
        if (WorkspaceLocator.TryLocate(null, orphan, out _, out _)) return; // a real one exists above temp; skip

        Assert.False(WorkspaceLocator.TryLocate(null, orphan, out _, out var error));
        Assert.Contains("--workspace", error, StringComparison.Ordinal);
        Assert.Contains("tap.md", error, StringComparison.Ordinal);
    }
}
