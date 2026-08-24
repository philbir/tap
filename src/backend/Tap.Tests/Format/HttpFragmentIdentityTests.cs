using Tap.Execution.Agent;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Tests.Format;

/// <summary>
/// A <c>.http</c> file breaks the one-file-one-WorkspaceFile assumption every path in the system
/// was built on. These tests pin down the identity rules that make several requests share a file
/// without anything downstream noticing: fragment paths index, resolve, and address; and the
/// fragment never leaks into an actual filesystem lookup.
/// </summary>
public class HttpFragmentIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-fragment").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private LoadedWorkspace Load() => new WorkspaceLoader().Load(_root);

    private void WriteWorkspace()
    {
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/_collection.tap", "---\nkind: collection\nname: demo\nbaseUrl: https://api.test\n---\n");
    }

    // ---- Composition -------------------------------------------------------------------------

    [Theory]
    [InlineData("Get Order", "get-order")]
    [InlineData("Get Order (v2)", "get-order-v2")]
    [InlineData("list-methods", "list-methods")]
    public void Fragments_are_slugified_so_they_survive_urls_and_shells(string name, string expected)
    {
        Assert.Equal($"orders.http#{expected}", HttpFragment.Compose("orders.http", name));
    }

    [Fact]
    public void Splitting_is_safe_on_paths_that_have_no_fragment()
    {
        Assert.Equal(("collections/demo/a.req.tap", null), HttpFragment.Split("collections/demo/a.req.tap"));
        Assert.Equal(("orders.http", "get-order"), HttpFragment.Split("orders.http#get-order"));
        Assert.False(HttpFragment.HasFragment("a.req.tap"));
    }

    // ---- Indexing and refs -------------------------------------------------------------------

    [Fact]
    public void Fragment_paths_are_indexed_and_found()
    {
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n\n### Create order\nPOST /orders\n");

        var ws = Load();

        Assert.Equal(2, ws.Requests.Count);
        Assert.NotNull(ws.FindByPath("collections/demo/orders.http#get-order"));
        Assert.NotNull(ws.FindByPath("collections/demo/orders.http#create-order"));
    }

    [Fact]
    public void A_flow_step_can_reference_a_fragment_path()
    {
        // The payoff of putting fragments in the path: flows and test sets reference them with
        // zero changes to the flow engine, because a ref is just a path.
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n");
        Write("tests/checkout.flow.tap",
            "---\nkind: flow\nname: checkout\nsteps:\n  - request: ../collections/demo/orders.http#get-order\n---\n");

        var ws = Load();

        var flow = Assert.Single(ws.Flows);
        var target = ws.Resolve(flow.Steps[0].Request, "tests");
        Assert.NotNull(target);
        Assert.Equal("Get order", target.Name);
    }

    [Fact]
    public void Collection_attribution_still_works_through_a_fragment()
    {
        // Attribution walks directories, and the fragment lives in the filename segment — so
        // Path.GetDirectoryName keeps returning collections/demo.
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n");

        var ws = Load();
        var owner = Tap.Workspace.Rendering.CollectionLocator.ForFile(ws, ws.Requests[0].RelativePath);

        Assert.NotNull(owner);
        Assert.Equal("demo", owner.Name);
    }

    [Fact]
    public void Fragments_stay_stable_when_a_request_is_added()
    {
        // An ordinal identity would renumber every request below an insertion, breaking every
        // flow and test set that referenced them. Names don't move.
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n");
        var before = Load().Requests[0].RelativePath;

        Write("collections/demo/orders.http", "### Brand new\nGET /new\n\n### Get order\nGET /orders/1\n");
        var after = Load().Requests.Single(r => r.Name == "Get order").RelativePath;

        Assert.Equal(before, after);
    }

    // ---- Filesystem resolution ---------------------------------------------------------------

    [Fact]
    public void The_fragment_is_stripped_before_touching_the_disk()
    {
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n");

        Assert.True(WorkspacePaths.TryResolve(_root, "collections/demo/orders.http#get-order", out var full, out var error), error);
        Assert.True(File.Exists(full));
        Assert.EndsWith("orders.http", full, StringComparison.Ordinal);
    }

    [Fact]
    public void Traversal_guards_still_apply_to_fragment_paths()
    {
        // Stripping the fragment must not become a way around the segment checks.
        Assert.False(WorkspacePaths.TryResolve(_root, "../outside.http#x", out _, out var error));
        Assert.Contains("'..'", error, StringComparison.Ordinal);
    }

    // ---- Addressing --------------------------------------------------------------------------

    private static bool Resolve(LoadedWorkspace ws, string query, out ResolvedTarget target, out string error)
        => TargetResolver.TryResolve(ws, query, [WorkspaceKind.Request], out target, out error);

    [Fact]
    public void A_request_resolves_by_fragment_path_and_by_name()
    {
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n\n### Create order\nPOST /orders\n");
        var ws = Load();

        Assert.True(Resolve(ws, "collections/demo/orders.http#get-order", out var byPath, out var e1), e1);
        Assert.Equal("Get order", byPath.File.Name);

        Assert.True(Resolve(ws, "Create order", out var byName, out var e2), e2);
        Assert.Equal("collections/demo/orders.http#create-order", byName.Path);
    }

    [Fact]
    public void A_bare_file_path_resolves_when_the_file_holds_one_request()
    {
        WriteWorkspace();
        Write("collections/demo/orders.http", "GET /orders\n");
        var ws = Load();

        Assert.True(Resolve(ws, "collections/demo/orders.http", out var target, out var error), error);
        // Canonical identity is still the fragment — the bare path is only a way of asking.
        Assert.Equal("collections/demo/orders.http#get-orders", target.Path);
    }

    [Fact]
    public void A_bare_file_path_with_several_requests_lists_them_instead_of_guessing()
    {
        WriteWorkspace();
        Write("collections/demo/orders.http", "### Get order\nGET /orders/1\n\n### Create order\nPOST /orders\n");
        var ws = Load();

        Assert.False(Resolve(ws, "collections/demo/orders.http", out _, out var error));
        Assert.Contains("holds 2 requests", error, StringComparison.Ordinal);
        Assert.Contains("#get-order", error, StringComparison.Ordinal);
        Assert.Contains("#create-order", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_filename_stem_still_addresses_a_single_request_file()
    {
        WriteWorkspace();
        Write("collections/demo/orders.http", "GET /orders\n");
        var ws = Load();

        Assert.True(Resolve(ws, "orders", out var target, out var error), error);
        Assert.Equal("collections/demo/orders.http#get-orders", target.Path);
    }
}
