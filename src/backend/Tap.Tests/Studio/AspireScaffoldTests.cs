using Tap.Studio;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Tests.Studio;

/// <summary>
/// First-run scaffolding. This code writes files into a folder the developer has committed to
/// their repository, on every start of every AppHost — so the properties that matter most here
/// are the ones about restraint: never touch what exists, never delete, and be a byte-identical
/// no-op the second time.
/// </summary>
public class AspireScaffoldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-scaffold").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path_(params string[] parts) => Path.Combine([_root, .. parts]);

    private static IReadOnlyDictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);

    [Fact]
    public void An_empty_folder_gets_a_manifest_a_collection_per_api_and_a_starter_request()
    {
        var result = AspireWorkspaceScaffold.Run(_root, ["orders-api", "billing-api"]);

        Assert.True(File.Exists(Path_("workspace.tap")));
        Assert.True(File.Exists(Path_("collections", "orders-api", "_collection.tap")));
        Assert.True(File.Exists(Path_("collections", "orders-api", "smoke.http")));
        Assert.True(File.Exists(Path_("collections", "billing-api", "_collection.tap")));
        Assert.True(File.Exists(Path_("collections", "billing-api", "smoke.http")));
        Assert.Equal(5, result.Created.Count);
    }

    [Fact]
    public void What_it_writes_actually_loads()
    {
        // A scaffold that produces a workspace with errors in it is worse than none.
        AspireWorkspaceScaffold.Run(_root, ["orders-api"]);

        var ws = new WorkspaceLoader().Load(_root);

        Assert.Empty(ws.Errors);
        Assert.NotNull(ws.Manifest);
        var collection = Assert.Single(ws.Collections);
        Assert.Equal("{{aspire:orders-api}}", collection.BaseUrl);

        // The starter is a real request with its assertion attached.
        var request = Assert.Single(ws.Requests);
        Assert.Equal("ping", request.Name);
        Assert.Single(request.Assertions);
    }

    [Fact]
    public void A_second_run_is_a_byte_identical_no_op()
    {
        AspireWorkspaceScaffold.Run(_root, ["orders-api"]);
        var before = Snapshot(_root);

        var second = AspireWorkspaceScaffold.Run(_root, ["orders-api"]);

        Assert.True(second.IsNoOp);
        Assert.Equal(before, Snapshot(_root));
    }

    [Fact]
    public void Adding_an_api_adds_exactly_that_collection()
    {
        AspireWorkspaceScaffold.Run(_root, ["orders-api"]);
        var before = Snapshot(_root);

        var result = AspireWorkspaceScaffold.Run(_root, ["orders-api", "billing-api"]);

        Assert.Equal(["billing-api/_collection.tap", "billing-api/smoke.http"], result.Created);
        // Everything that already existed is untouched.
        foreach (var (path, content) in before)
            Assert.Equal(content, File.ReadAllText(Path.Combine(_root, path)));
    }

    [Fact]
    public void Removing_an_api_deletes_nothing()
    {
        AspireWorkspaceScaffold.Run(_root, ["orders-api", "billing-api"]);
        var before = Snapshot(_root);

        AspireWorkspaceScaffold.Run(_root, ["orders-api"]);

        Assert.Equal(before, Snapshot(_root));
    }

    [Fact]
    public void Existing_files_are_never_overwritten()
    {
        Directory.CreateDirectory(Path_("collections", "orders-api"));
        File.WriteAllText(Path_("collections", "orders-api", "_collection.tap"),
            "---\nkind: collection\nname: my own\nbaseUrl: https://i-set-this.test\n---\n");
        File.WriteAllText(Path_("collections", "orders-api", "smoke.http"), "### mine\nGET /mine\n");

        var result = AspireWorkspaceScaffold.Run(_root, ["orders-api"]);

        Assert.Equal(["workspace.tap"], result.Created);
        Assert.Contains("i-set-this.test", File.ReadAllText(Path_("collections", "orders-api", "_collection.tap")), StringComparison.Ordinal);
        Assert.Equal("### mine\nGET /mine\n", File.ReadAllText(Path_("collections", "orders-api", "smoke.http")));
    }

    [Fact]
    public void A_legacy_manifest_is_recognized_so_no_second_one_appears()
    {
        File.WriteAllText(Path_("tap.md"), "---\nkind: workspace\nname: legacy\n---\n");

        var result = AspireWorkspaceScaffold.Run(_root, Array.Empty<string>());

        Assert.True(result.IsNoOp);
        Assert.False(File.Exists(Path_("workspace.tap")));
    }

    [Fact]
    public void No_apis_still_produces_a_usable_workspace()
    {
        var result = AspireWorkspaceScaffold.Run(_root, Array.Empty<string>());

        Assert.Equal(["workspace.tap"], result.Created);
        Assert.Empty(new WorkspaceLoader().Load(_root).Errors);
    }
}
