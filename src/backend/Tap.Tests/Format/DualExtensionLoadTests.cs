using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Tests.Format;

/// <summary>
/// The 0.7.0 compatibility contract, exercised against a real folder on disk: a workspace may
/// hold both extension families at once, because that is what every user has partway through
/// <c>tap-studio migrate</c>. Legacy files must load exactly as before, warn rather than fail,
/// and refs must resolve across the family boundary in both directions.
/// </summary>
public class DualExtensionLoadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-dual-ext").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Request(string name, string auth = "") => $"""
        ---
        kind: request
        name: {name}
        {auth}
        ---

        ```http
        GET /things
        ```
        """;

    private static string Auth(string name) => $"""
        ---
        kind: auth
        name: {name}
        type: bearer
        token: t
        ---
        """;

    private LoadedWorkspace Load() => new WorkspaceLoader().Load(_root);

    [Fact]
    public void A_mixed_extension_workspace_loads_every_file()
    {
        Write("workspace.tap", "---\nkind: workspace\nname: mixed\n---\n");
        Write("collections/demo/_collection.md", "---\nkind: collection\nname: demo\nbaseUrl: https://example.test\n---\n");
        Write("collections/demo/canonical.req.tap", Request("canonical"));
        Write("collections/demo/legacy.req.md", Request("legacy"));
        Write("auth/admin.auth.md", Auth("admin"));
        Write("environments/dev.env.tap", "---\nkind: env\nname: dev\n---\n");

        var ws = Load();

        Assert.Equal(2, ws.Requests.Count);
        Assert.Contains(ws.Requests, r => r.Name == "canonical");
        Assert.Contains(ws.Requests, r => r.Name == "legacy");
        Assert.NotNull(ws.Manifest);
        Assert.Single(ws.Collections);
        Assert.Single(ws.Auths);
        Assert.Single(ws.Environments);
    }

    [Fact]
    public void Legacy_files_warn_but_never_fail_the_load()
    {
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/legacy.req.md", Request("legacy"));

        var ws = Load();

        var warning = Assert.Single(ws.Errors);
        Assert.Equal(WorkspaceErrorCode.W_LEGACY_EXTENSION, warning.Code);
        Assert.Equal(WorkspaceErrorSeverity.Warning, warning.Severity);
        Assert.Equal("collections/demo/legacy.req.md", warning.RelativePath);
        // The message has to name the command that fixes it, since that's the only way a user
        // learns the migration exists.
        Assert.Contains("tap-studio migrate", warning.Message, StringComparison.Ordinal);
        Assert.Contains("legacy.req.tap", warning.Message, StringComparison.Ordinal);

        // The file itself still loaded.
        Assert.Single(ws.Requests);
    }

    [Fact]
    public void A_fully_migrated_workspace_is_completely_silent()
    {
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/_collection.tap", "---\nkind: collection\nname: demo\nbaseUrl: https://example.test\n---\n");
        Write("collections/demo/orders.req.tap", Request("orders"));

        Assert.Empty(Load().Errors);
    }

    [Fact]
    public void When_both_spellings_exist_the_canonical_one_wins()
    {
        // What a half-finished migration leaves behind. Loading both would double the request.
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/orders.req.tap", Request("from-canonical"));
        Write("collections/demo/orders.req.md", Request("from-legacy"));

        var ws = Load();

        var request = Assert.Single(ws.Requests);
        Assert.Equal("from-canonical", request.Name);

        var error = Assert.Single(ws.Errors);
        Assert.Equal(WorkspaceErrorCode.E_EXTENSION_COLLISION, error.Code);
        Assert.Equal(WorkspaceErrorSeverity.Error, error.Severity);
        Assert.Equal("collections/demo/orders.req.md", error.RelativePath);
    }

    [Fact]
    public void The_winner_does_not_depend_on_walk_order()
    {
        // Same scenario as above but with names that sort the other way, since the walk order
        // is filesystem-dependent and "first one wins" would be a coin flip.
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/zzz.req.md", Request("legacy-z"));
        Write("collections/demo/zzz.req.tap", Request("canonical-z"));
        Write("collections/demo/aaa.req.md", Request("legacy-a"));
        Write("collections/demo/aaa.req.tap", Request("canonical-a"));

        var ws = Load();

        Assert.Equal(2, ws.Requests.Count);
        Assert.All(ws.Requests, r => Assert.StartsWith("canonical-", r.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Refs_resolve_across_the_family_boundary_in_both_directions()
    {
        // Refs are literal relative path strings carrying an extension, so a workspace mid-
        // migration necessarily has canonical files pointing at legacy ones and vice versa.
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("auth/legacy.auth.md", Auth("legacy-auth"));
        Write("auth/canonical.auth.tap", Auth("canonical-auth"));
        Write("collections/demo/_collection.tap", "---\nkind: collection\nname: demo\nbaseUrl: https://example.test\n---\n");
        Write("collections/demo/points-at-legacy.req.tap", Request("a", "auth: ../../auth/legacy.auth.md"));
        Write("collections/demo/points-at-canonical.req.md", Request("b", "auth: ../../auth/canonical.auth.tap"));

        var ws = Load();

        foreach (var name in new[] { "a", "b" })
        {
            var request = Assert.Single(ws.Requests, r => r.Name == name);
            var target = ws.Resolve(request.Auth, Path.GetDirectoryName(request.RelativePath)!.Replace('\\', '/'));
            Assert.NotNull(target);
        }
    }

    [Fact]
    public void A_legacy_collection_still_owns_the_requests_beside_it()
    {
        // Collection attribution is a path lookup for the collection file, so it has to try
        // both spellings or an unmigrated collection silently stops applying its baseUrl.
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/_collection.md", "---\nkind: collection\nname: demo\nbaseUrl: https://example.test\n---\n");
        Write("collections/demo/orders.req.tap", Request("orders"));

        var ws = Load();
        var owner = CollectionLocator.ForFile(ws, "collections/demo/orders.req.tap");

        Assert.NotNull(owner);
        Assert.Equal("demo", owner.Name);
    }

    [Fact]
    public void A_saved_file_lands_on_the_legacy_path_when_that_is_what_exists()
    {
        // Clicking Save must not rename the user's file out from under their refs.
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("collections/demo/orders.req.md", Request("orders"));

        var ws = Load();

        Assert.Equal("collections/demo/orders.req.md", ws.ResolveWritePath("collections/demo/orders.req.tap"));
        // A file that doesn't exist yet is created canonically.
        Assert.Equal("collections/demo/new.req.tap", ws.ResolveWritePath("collections/demo/new.req.tap"));
    }
}
