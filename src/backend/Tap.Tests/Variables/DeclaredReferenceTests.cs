using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;
using Tap.Tests.Agent;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Variables;

/// <summary>
/// A declared variable may hold a reference to another one — which is what makes
/// <c>secret: true</c> work now that marking a variable secret moves its value into a provider
/// and leaves <c>default: '{{file:stripe.key}}'</c> behind (§12.6). Before that resolved, the
/// token itself went out on the wire.
///
/// <para>The line these tests hold is <em>declared</em>: a value a workspace file wrote is a
/// template, a value that arrived at runtime is data. A response that comes back carrying
/// <c>{{file:stripe.key}}</c> must never get to choose what the next request sends.</para>
/// </summary>
public class DeclaredReferenceTests
{
    private static readonly WorkspaceFile Env = Parse("environments/dev.env.tap", """
        ---
        kind: env
        name: Dev
        vars:
          apiToken: { default: '{{stub:kv.token}}', secret: true }
          host: api.demo.test
        ---
        """);

    private static readonly WorkspaceFile Request = Parse("collections/demo/create.req.tap", """
        ---
        kind: request
        name: Create thing
        ---

        ```http
        POST /things
        X-Api-Token: {{apiToken}}
        Content-Type: application/json

        {"token":"{{apiToken}}"}
        ```
        """);

    private static VariableProviderRegistry Providers()
        => Registry(new StubVariableProvider(
            "stub", new VariableValue("kv.token", "resolved-token-value-42", IsSecret: true, "stub")));

    private static ValueTask<ResolvedRequest> RenderAsync(
        WorkspaceFile request,
        WorkspaceFile? env = null,
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlySet<string>? declaredOverrides = null,
        params WorkspaceFile[] extra)
    {
        env ??= Env;
        var ws = BuildWorkspace([DemoCollection(defaultAuth: null), env, request, .. extra]);
        var renderer = new WorkspaceRenderer(ws, Providers());
        return renderer.RenderAsync(
            (RequestFile)request, (EnvFile)env, overrides, CancellationToken.None,
            declaredOverrides: declaredOverrides);
    }

    [Fact]
    public async Task An_env_variable_holding_a_provider_reference_resolves()
    {
        var rendered = await RenderAsync(Request);

        Assert.Equal("resolved-token-value-42", rendered.Headers["X-Api-Token"]);
        Assert.Contains("resolved-token-value-42", rendered.Body);
        Assert.DoesNotContain("{{stub:kv.token}}", rendered.Body);
    }

    [Fact]
    public async Task The_redactor_still_knows_what_came_back()
    {
        // The value never appeared in the file, so the only thing that can flag it is the
        // provider — and it has to reach the redactor through the reference all the same.
        var rendered = await RenderAsync(Request);
        Assert.Equal(SecretRedactor.Mask, rendered.Redactor.Redact("resolved-token-value-42"));
    }

    [Fact]
    public async Task A_reference_chain_resolves_the_whole_way_down()
    {
        var request = Parse("collections/demo/chain.req.tap", """
            ---
            kind: request
            name: Chain
            vars:
              auth: 'Bearer {{apiToken}}'
            ---

            ```http
            GET /things
            Authorization: {{auth}}
            ```
            """);

        var rendered = await RenderAsync(request);
        Assert.Equal("Bearer resolved-token-value-42", rendered.Headers["Authorization"]);
    }

    [Fact]
    public async Task A_reference_that_closes_on_itself_is_named_not_looped()
    {
        var env = Parse("environments/cycle.env.tap", """
            ---
            kind: env
            name: Cycle
            vars:
              a: '{{b}}'
              b: '{{a}}'
            ---
            """);
        var request = Parse("collections/demo/cycle.req.tap", """
            ---
            kind: request
            name: Cycle
            ---

            ```http
            GET /things/{{a}}
            ```
            """);

        var ex = await Assert.ThrowsAsync<WorkspaceParseException>(
            async () => await RenderAsync(request, env));

        Assert.Equal(WorkspaceErrorCode.E_VAR_CYCLE, ex.Error.Code);
        Assert.Contains("a → b → a", ex.Error.Message);
    }

    [Fact]
    public async Task A_portable_http_variable_may_reference_too()
    {
        // `@name = value` lines sit at the bottom of the cascade but are still authored in the
        // file, so they are templates like every other declaration.
        var parsed = Tap.Workspace.Parsing.HttpFileParser.Parse("collections/demo/portable.http", """
            @token = {{stub:kv.token}}

            ### Create
            # @name create
            GET /things
            X-Api-Token: {{token}}
            """);
        Assert.Empty(parsed.Errors);

        var rendered = await RenderAsync(Assert.Single(parsed.Requests));
        Assert.Equal("resolved-token-value-42", rendered.Headers["X-Api-Token"]);
    }

    // -------------------------------------------------------------------------------------
    // The other half: what arrived at runtime stays exactly as it arrived.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_value_the_run_produced_is_never_re_scanned()
    {
        var request = Parse("collections/demo/echo.req.tap", """
            ---
            kind: request
            name: Echo
            ---

            ```http
            POST /things
            Content-Type: text/plain

            {{fromResponse}}
            ```
            """);

        // What a flow step bound with `extract:` — a response body that happens to name a
        // secret. Expanding it would let the upstream pick which secret the next request carries.
        var rendered = await RenderAsync(
            request, overrides: new Dictionary<string, string> { ["fromResponse"] = "{{stub:kv.token}}" });

        Assert.Equal("{{stub:kv.token}}", rendered.Body);
    }

    [Fact]
    public async Task A_declared_override_is_a_template_because_the_caller_vouched_for_it()
    {
        // A test set's own `vars:` travel the same tier as extracted values, so the runner
        // names the ones that came out of a file.
        var request = Parse("collections/demo/echo.req.tap", """
            ---
            kind: request
            name: Echo
            ---

            ```http
            POST /things
            Content-Type: text/plain

            {{setVar}}
            ```
            """);

        var rendered = await RenderAsync(
            request,
            overrides: new Dictionary<string, string> { ["setVar"] = "{{stub:kv.token}}" },
            declaredOverrides: new HashSet<string>(StringComparer.Ordinal) { "setVar" });

        Assert.Equal("resolved-token-value-42", rendered.Body);
    }

    [Fact]
    public async Task An_override_beats_a_declaration_and_takes_its_literalness_with_it()
    {
        // `--var apiToken=…` replaces what the env declared. The value is the caller's, so it
        // is data even though the name it lands on was declared in a file.
        var rendered = await RenderAsync(
            Request, overrides: new Dictionary<string, string> { ["apiToken"] = "{{stub:kv.token}}" });

        Assert.Equal("{{stub:kv.token}}", rendered.Headers["X-Api-Token"]);
    }
}
