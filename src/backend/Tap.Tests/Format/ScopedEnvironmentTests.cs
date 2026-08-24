using Tap.Execution.Auth;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Format;

/// <summary>
/// Environments replaced collection stages in 0.7.0, absorbing the two things a stage could do
/// that an env could not: override the collection's <c>baseUrl</c> and its <c>defaultAuth</c>.
/// What keeps that from turning every environment into a workspace-wide footgun is the
/// <c>collections:</c> scope — so these tests pin the scope rule as hard as the override rules.
///
/// <para>The failure mode being guarded against is silent in every direction: an out-of-scope
/// env that still contributed would send a request to another collection's host, and an
/// in-scope env that didn't would leave the picker doing nothing.</para>
/// </summary>
public class ScopedEnvironmentTests
{
    private const string RelativeRequest = """
        ---
        kind: request
        name: Ping
        ---

        ```http
        GET /ping
        ```
        """;

    private static RequestFile Request(string path = "collections/demo/ping.req.tap")
        => (RequestFile)Parse(path, RelativeRequest);

    private static EnvFile Env(string path, string body)
        => (EnvFile)Parse(path, body);

    private static async Task<ResolvedRequest> RenderAsync(EnvFile? env, params WorkspaceFile[] extra)
    {
        var request = Request();
        var files = new List<WorkspaceFile> { DemoCollection(defaultAuth: null), request };
        files.AddRange(extra);
        if (env is not null) files.Add(env);
        var renderer = new WorkspaceRenderer(BuildWorkspace([.. files]), Registry());
        return await renderer.RenderAsync(request, env, overrides: null, CancellationToken.None);
    }

    // ---- Scope ---------------------------------------------------------------------------

    [Fact]
    public async Task An_env_scoped_to_this_collection_applies()
    {
        var env = Env("collections/demo/uat.env.tap", """
            ---
            kind: env
            name: UAT
            collections:
            - collection: demo
              baseUrl: http://uat.demo.test
            vars:
              who: uat
            ---
            """);

        var rendered = await RenderAsync(env);

        Assert.Equal("http://uat.demo.test/ping", rendered.Url);
        Assert.Equal("collections/demo/uat.env.tap", rendered.Metadata.EnvPath);
    }

    [Fact]
    public async Task An_env_scoped_to_another_collection_drops_out_entirely()
    {
        // Not an error: a test set spanning collections runs under one --env, and the scoped
        // entry is expected to bow out for the collections it wasn't written for. What it must
        // never do is contribute the other collection's baseUrl.
        var env = Env("collections/github/uat.env.tap", """
            ---
            kind: env
            name: GitHub UAT
            collections:
            - collection: github
              baseUrl: http://uat.github.test
            ---
            """);

        var rendered = await RenderAsync(env);

        Assert.Equal("http://api.demo.test/ping", rendered.Url);
        Assert.Null(rendered.Metadata.EnvPath);
    }

    [Fact]
    public async Task An_env_with_no_collections_is_global_and_overrides_nothing()
    {
        // A global env carries variables and provider bindings only. There is no
        // environment-wide baseUrl to set: it would move every collection at once.
        var env = Env("environments/prod.env.tap", """
            ---
            kind: env
            name: Prod
            vars:
              who: prod
            ---
            """);

        var rendered = await RenderAsync(env);

        Assert.True(env.IsGlobal);
        Assert.Equal("http://api.demo.test/ping", rendered.Url);
        Assert.Equal("environments/prod.env.tap", rendered.Metadata.EnvPath);
    }

    [Fact]
    public async Task Each_assignment_carries_its_own_baseUrl()
    {
        // The reason the override lives on the assignment rather than on the env: one `uat`
        // means a different host in each collection it is assigned to.
        var env = Env("environments/uat.env.tap", """
            ---
            kind: env
            name: UAT
            collections:
            - collection: demo
              baseUrl: http://uat.demo.test
            - collection: github
              baseUrl: http://uat.github.test
            ---
            """);

        Assert.Equal("http://uat.demo.test", env.BindingFor("demo")!.BaseUrl);
        Assert.Equal("http://uat.github.test", env.BindingFor("github")!.BaseUrl);
        Assert.Null(env.BindingFor("streams"));

        var rendered = await RenderAsync(env);
        Assert.Equal("http://uat.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task A_bare_slug_assigns_without_overriding_anything()
    {
        var env = Env("environments/shared.env.tap", """
            ---
            kind: env
            name: Shared
            collections: [demo]
            vars:
              who: shared
            ---
            """);

        var binding = Assert.Single(env.Collections);
        Assert.Equal("demo", binding.Collection);
        Assert.True(binding.IsBare);

        var rendered = await RenderAsync(env);
        Assert.Equal("http://api.demo.test/ping", rendered.Url);
    }

    [Fact]
    public void EnvironmentsFor_offers_the_globals_plus_the_collections_own()
    {
        var global = Env("environments/dev.env.tap", "---\nkind: env\nname: Dev\n---\n");
        var mine = Env("collections/demo/uat.env.tap", "---\nkind: env\nname: UAT\ncollections: [demo]\n---\n");
        var theirs = Env("collections/github/qa.env.tap", "---\nkind: env\nname: QA\ncollections: [github]\n---\n");
        var ws = BuildWorkspace(DemoCollection(defaultAuth: null), global, mine, theirs);

        Assert.Equal(["Dev", "UAT"], ws.EnvironmentsFor("demo").Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Dev", "QA"], ws.EnvironmentsFor("github").Select(e => e.Name).Order(StringComparer.Ordinal));

        // A request that belongs to no collection still sees every global one.
        Assert.Equal(["Dev"], ws.EnvironmentsFor(null).Select(e => e.Name));
    }

    // ---- Overrides -----------------------------------------------------------------------

    [Fact]
    public async Task An_env_without_a_baseUrl_inherits_the_collections()
    {
        var env = Env("collections/demo/vars-only.env.tap", """
            ---
            kind: env
            name: Vars only
            collections:
            - collection: demo
            vars:
              who: someone
            ---
            """);

        var rendered = await RenderAsync(env);

        Assert.Equal("http://api.demo.test/ping", rendered.Url);
    }

    [Fact]
    public async Task An_envs_defaultAuth_outranks_the_collections()
    {
        // The ref is written relative to the env file, which is what lets an env in
        // environments/ point at a profile the collection never mentions.
        var env = Env("collections/demo/uat.env.tap", """
            ---
            kind: env
            name: UAT
            collections:
            - collection: demo
              defaultAuth: ./uat.auth.tap
            ---
            """);
        var uatAuth = Parse("collections/demo/uat.auth.tap", """
            ---
            kind: auth
            name: UAT Bearer
            type: bearer
            token: uat-token
            ---
            """);
        var ws = BuildWorkspace(
            DemoCollection(defaultAuth: "./bearer.auth.tap"), BearerAuth, uatAuth, Request(), env);
        var request = (RequestFile)ws.FindByPath("collections/demo/ping.req.tap")!;

        var rendered = await new WorkspaceRenderer(ws, Registry())
            .RenderAsync(request, env, overrides: null, CancellationToken.None);

        Assert.Equal("Bearer uat-token", rendered.Headers["Authorization"]);
        // …and the pipeline's token-injection path has to agree, or a runtime-token profile
        // would be rendered from one profile and stamped from another.
        Assert.Equal("collections/demo/uat.auth.tap",
            RequestPipeline.ResolveAuth(ws, request, env.RelativePath)?.RelativePath);
    }

    [Fact]
    public void An_out_of_scope_envs_defaultAuth_is_ignored()
    {
        var env = Env("collections/github/qa.env.tap", """
            ---
            kind: env
            name: QA
            collections:
            - collection: github
              defaultAuth: ./qa.auth.tap
            ---
            """);
        var qaAuth = Parse("collections/github/qa.auth.tap", """
            ---
            kind: auth
            name: QA Bearer
            type: bearer
            token: qa-token
            ---
            """);
        var ws = BuildWorkspace(
            DemoCollection(defaultAuth: "./bearer.auth.tap"), BearerAuth, qaAuth, Request(), env);
        var request = (RequestFile)ws.FindByPath("collections/demo/ping.req.tap")!;

        Assert.Equal("collections/demo/bearer.auth.tap",
            RequestPipeline.ResolveAuth(ws, request, env.RelativePath)?.RelativePath);
    }

    // ---- Auth scope ------------------------------------------------------------------------

    [Fact]
    public void A_profiles_auth_context_follows_its_own_collection_not_the_callers()
    {
        // A request in `demo` borrowing a profile from `github` must not drag demo's environment
        // across: that env's variables mean something different inside github.
        var demoEnv = Env("collections/demo/uat.env.tap", "---\nkind: env\nname: UAT\ncollections: [demo]\n---\n");
        var githubAuth = Parse("collections/github/pat.auth.tap", """
            ---
            kind: auth
            name: GitHub PAT
            type: bearer
            token: gh
            ---
            """);
        var ws = BuildWorkspace(DemoCollection(defaultAuth: null), githubAuth, demoEnv);

        var context = AuthScopeResolver.ContextFor(ws, githubAuth.RelativePath, demoEnv.RelativePath);

        Assert.Null(context.Env);
        Assert.Null(context.ScopeFor(githubAuth.RelativePath).Env);
    }

    // ---- Removed syntax ----------------------------------------------------------------------

    [Theory]
    [InlineData("stages:\n- name: uat\n")]
    [InlineData("defaultStage: uat\n")]
    public void A_collection_still_carrying_stages_fails_to_parse(string block)
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => FileParser.Parse(
            "collections/demo/_collection.tap",
            $"---\nkind: collection\nname: Demo\nbaseUrl: http://api.demo.test\n{block}---\n"));

        Assert.Equal(WorkspaceErrorCode.E_UNKNOWN_FIELD, ex.Error.Code);
        Assert.Contains("collection: <slug>", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_env_naming_a_path_instead_of_a_slug_is_rejected()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => FileParser.Parse(
            "environments/uat.env.tap",
            "---\nkind: env\nname: UAT\ncollections: [collections/demo/_collection.tap]\n---\n"));

        Assert.Equal(WorkspaceErrorCode.E_UNKNOWN_FIELD, ex.Error.Code);
        Assert.Contains("collection slugs", ex.Error.Message, StringComparison.Ordinal);
        Assert.Contains("'demo'", ex.Error.Message, StringComparison.Ordinal);
    }

    // ---- Emitter round-trip ------------------------------------------------------------------

    [Fact]
    public void The_emitter_writes_a_bare_slug_when_an_assignment_overrides_nothing()
    {
        // "This env is offered here" is the common case; spelling it as a one-key mapping would
        // be noise in every diff.
        var source = EnvSpecEmitter.ToFileSource(new EnvSpecDto
        {
            Path = "environments/uat.env.tap",
            Name = "UAT",
            Collections =
            [
                new EnvCollectionDto("billing", null, null),
                new EnvCollectionDto("orders", "https://orders-uat.acme.test", "../../auth/uat.auth.tap"),
            ],
        });

        Assert.Contains("- billing", source, StringComparison.Ordinal);
        Assert.Contains("- collection: orders", source, StringComparison.Ordinal);

        var parsed = (EnvFile)FileParser.Parse("environments/uat.env.tap", source);
        Assert.Equal(2, parsed.Collections.Count);
        Assert.True(parsed.BindingFor("billing")!.IsBare);
        Assert.Equal("https://orders-uat.acme.test", parsed.BindingFor("orders")!.BaseUrl);
        Assert.Equal("../../auth/uat.auth.tap", parsed.BindingFor("orders")!.DefaultAuth!.RelativePath);
    }

    [Fact]
    public void An_env_with_no_assignments_emits_no_collections_key()
    {
        var source = EnvSpecEmitter.ToFileSource(new EnvSpecDto
        {
            Path = "environments/dev.env.tap",
            Name = "Dev",
            Vars = new Dictionary<string, string> { ["who"] = "dev" },
        });

        Assert.DoesNotContain("collections:", source, StringComparison.Ordinal);
        Assert.True(((EnvFile)FileParser.Parse("environments/dev.env.tap", source)).IsGlobal);
    }

    [Fact]
    public void Assigning_the_same_collection_twice_is_rejected()
    {
        // Two entries would make "the baseUrl for demo" ambiguous, and first-one-wins is not a
        // rule anyone should have to discover.
        var ex = Assert.Throws<WorkspaceParseException>(() => FileParser.Parse(
            "environments/uat.env.tap",
            """
            ---
            kind: env
            name: UAT
            collections:
            - collection: demo
              baseUrl: http://one.test
            - collection: demo
              baseUrl: http://two.test
            ---
            """));

        Assert.Equal(WorkspaceErrorCode.E_UNKNOWN_FIELD, ex.Error.Code);
        Assert.Contains("assigned twice", ex.Error.Message, StringComparison.Ordinal);
    }
}
