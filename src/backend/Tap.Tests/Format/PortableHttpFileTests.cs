using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Format;

/// <summary>
/// The two-way portability contract for <c>.http</c> files: the same file has to run in Visual
/// Studio / REST Client, where its own <c>@baseUrl</c> is the only definition there is, and in
/// Tap, where the collection and the selected environment must win instead.
///
/// <para>That is only possible because a file variable is the <em>weakest</em> scope rather than
/// the strongest, and because Tap binds a built-in <c>{{baseUrl}}</c>. Get either half wrong and
/// the failure is silent: the request still sends, just to the wrong host — which is exactly the
/// failure these tests exist to catch.</para>
/// </summary>
public class PortableHttpFileTests
{
    /// <summary>The shape both scaffolds emit, and the shape a user brings in from another tool:
    /// a standalone fallback on top, relative-looking request lines built from it below.</summary>
    private const string PortableFile = """
        @baseUrl = http://localhost:5000

        ### Ping
        # @name ping
        GET {{baseUrl}}/ping
        Accept: application/json
        """;

    private static RequestFile ParseOne(string content, string path = "collections/demo/smoke.http")
    {
        var result = HttpFileParser.Parse(path, content);
        Assert.Empty(result.Errors);
        return Assert.Single(result.Requests);
    }

    private static async Task<ResolvedRequest> RenderAsync(
        RequestFile request, WorkspaceFile collection, EnvFile? env = null)
    {
        var files = new List<WorkspaceFile> { collection, request };
        if (env is not null) files.Add(env);
        var renderer = new WorkspaceRenderer(BuildWorkspace([.. files]), Registry());
        return await renderer.RenderAsync(request, env, overrides: null, CancellationToken.None);
    }

    // ---- The collection wins over the file's fallback -----------------------------------------

    [Fact]
    public async Task Inside_tap_the_collection_baseUrl_beats_the_files_own()
    {
        // The whole point. If the file's @baseUrl won here, dropping a .http file into a
        // collection would quietly keep talking to localhost:5000.
        var request = ParseOne(PortableFile);

        var rendered = await RenderAsync(request, DemoCollection(defaultAuth: null));

        Assert.Equal("http://api.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task Selecting_an_environment_moves_a_portable_request()
    {
        // An environment that did nothing to a file carrying its own baseUrl would make the
        // environment picker a lie.
        var request = ParseOne(PortableFile);

        var rendered = await RenderAsync(
            request, DemoCollection(defaultAuth: null), env: (EnvFile)UatEnv);

        Assert.Equal("http://uat.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task Outside_a_collection_the_files_own_baseUrl_is_what_answers()
    {
        // No collection, so nothing outranks the file — which is the standalone case, and the
        // reason the fallback is written into the file in the first place.
        var request = ParseOne(PortableFile, "smoke.http");

        var renderer = new WorkspaceRenderer(BuildWorkspace(request), Registry());
        var rendered = await renderer.RenderAsync(request, null, null, CancellationToken.None);

        Assert.Equal("http://localhost:5000/ping", rendered.Url);
    }

    [Fact]
    public async Task An_environment_still_overrides_a_portable_var()
    {
        // Portable sits below env too, not just below the collection.
        var env = (EnvFile)Parse("environments/dev.env.tap", """
            ---
            kind: env
            name: Dev
            vars:
              baseUrl: http://env.demo.test
            ---
            """);
        var request = ParseOne(PortableFile);

        var rendered = await RenderAsync(request, DemoCollection(defaultAuth: null), env: env);

        Assert.Equal("http://env.demo.test/ping", rendered.Url);
    }

    // ---- The built-in binding -----------------------------------------------------------------

    [Fact]
    public async Task BaseUrl_is_bound_even_when_the_file_never_declared_it()
    {
        // A file that only ever ran inside Tap can use {{baseUrl}} too — the built-in is not
        // conditional on a portable declaration existing.
        var request = ParseOne("""
            ### Ping
            GET {{baseUrl}}/ping
            """);

        var rendered = await RenderAsync(request, DemoCollection(defaultAuth: null));

        Assert.Equal("http://api.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task The_bound_baseUrl_has_no_trailing_slash_so_joins_stay_clean()
    {
        var request = ParseOne("""
            ### Ping
            GET {{baseUrl}}/ping
            """);

        var rendered = await RenderAsync(request, DemoCollection(baseUrl: "http://api.demo.test/", defaultAuth: null));

        Assert.Equal("http://api.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task An_author_who_declares_their_own_baseUrl_var_keeps_it()
    {
        // The built-in fills a gap; it must not overrule a workspace that already means
        // something specific by the name.
        var collection = Parse("collections/demo/_collection.tap", """
            ---
            kind: collection
            name: Demo
            baseUrl: 'http://api.demo.test'
            vars:
              baseUrl: http://authors-choice.test
            ---
            """);
        var request = ParseOne("""
            ### Ping
            GET {{baseUrl}}/ping
            """);

        var rendered = await RenderAsync(request, collection);

        Assert.Equal("http://authors-choice.test/ping", rendered.Url);
    }

    [Fact]
    public async Task A_relative_request_line_still_joins_the_collection_baseUrl()
    {
        // The old spelling keeps working — this change adds a portable option, it does not
        // require one.
        var request = ParseOne("""
            ### Ping
            GET /ping
            """);

        var rendered = await RenderAsync(request, DemoCollection(defaultAuth: null));

        Assert.Equal("http://api.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task A_tap_authored_request_keeps_its_vars_at_the_strongest_scope()
    {
        // The inversion is scoped to portable files. A .req.tap's `vars:` are authored for Tap
        // and must still beat the collection.
        var collection = Parse("collections/demo/_collection.tap", """
            ---
            kind: collection
            name: Demo
            baseUrl: 'http://api.demo.test'
            vars:
              which: collection
            ---
            """);
        var request = (RequestFile)Parse("collections/demo/get.req.tap", """
            ---
            kind: request
            name: Get
            vars:
              which: request
            ---

            ```http
            GET /x/{{which}}
            ```
            """);

        var renderer = new WorkspaceRenderer(BuildWorkspace(collection, request), Registry());
        var rendered = await renderer.RenderAsync(request, null, null, CancellationToken.None);

        Assert.Equal("http://api.demo.test/x/request", rendered.Url);
    }

    // ---- The variables panel must agree with the renderer -------------------------------------

    [Fact]
    public async Task The_variable_view_reports_the_same_baseUrl_the_renderer_sends_to()
    {
        // These are two separate code paths over the same rules, and a disagreement here is the
        // worst kind: the panel tells you the request goes to localhost:5000 while it actually
        // goes somewhere else. Pin them together.
        var request = ParseOne(PortableFile);
        var collection = DemoCollection(defaultAuth: null);
        var workspace = BuildWorkspace(collection, request);

        var view = await VariableViewBuilder.BuildAsync(
            workspace, Registry(), CancellationToken.None, requestPath: request.RelativePath);
        var rendered = await new WorkspaceRenderer(workspace, Registry())
            .RenderAsync(request, null, null, CancellationToken.None);

        var shown = view.Result.Single(v => v.Name == WorkspaceRenderer.BaseUrlVariable);
        Assert.Equal("http://api.demo.test", shown.Value);
        Assert.StartsWith(shown.Value!, rendered.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_files_own_vars_are_listed_under_the_portable_scope()
    {
        // Surfacing them as `request` scope would tell the reader they are the strongest
        // definition, which is the opposite of what the cascade does with them.
        var request = ParseOne(PortableFile);
        var workspace = BuildWorkspace(DemoCollection(defaultAuth: null), request);

        var view = await VariableViewBuilder.BuildAsync(
            workspace, Registry(), CancellationToken.None, requestPath: request.RelativePath);

        var portable = Assert.Single(view.Sets, s => s.Scope == VariableScope.Portable);
        Assert.Equal("smoke.http", portable.Label);
        Assert.Contains(portable.Variables, v => v.Name == "baseUrl");
    }

    // ---- Non-baseUrl portable vars ------------------------------------------------------------

    [Fact]
    public async Task A_portable_var_nobody_overrides_still_resolves()
    {
        // The common case: a file variable that names a path fragment or a payload value. Nothing
        // in the workspace speaks to it, so the file's own definition is the answer.
        var request = ParseOne("""
            @ordersPath = /orders

            ### List
            GET {{ordersPath}}/open
            """);

        var rendered = await RenderAsync(request, DemoCollection(defaultAuth: null));

        Assert.Equal("http://api.demo.test/orders/open", rendered.Url);
    }
}
