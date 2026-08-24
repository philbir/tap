using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Agent;

/// <summary>
/// The render path must hand every caller a redactor that already knows this render's
/// secrets — cascade vars marked secret, provider hits flagged secret, and the headers the
/// auth profile contributed — without any of that ever landing in the metadata that gets
/// persisted.
/// </summary>
public class RenderRedactionTests
{
    private static readonly WorkspaceFile DevEnv = Parse("environments/dev.env.tap", """
        ---
        kind: env
        name: Dev
        vars:
          API_TOKEN: { default: env-secret-value-123, secret: true }
        ---
        """);

    private static readonly WorkspaceFile PostRequest = Parse("collections/demo/create.req.tap", """
        ---
        kind: request
        name: Create thing
        ---

        ```http
        POST /things
        X-Api-Token: {{API_TOKEN}}
        Content-Type: application/json

        {"token":"{{stub:kv.secret}}"}
        ```
        """);

    private static async Task<ResolvedRequest> RenderAsync()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth, DevEnv, PostRequest);
        var registry = Registry(new StubVariableProvider(
            "stub", new VariableValue("kv.secret", "provider-secret-value-9", IsSecret: true, "stub")));
        var renderer = new WorkspaceRenderer(ws, registry);
        return await renderer.RenderAsync(
            (RequestFile)PostRequest, (EnvFile)DevEnv, overrides: null, CancellationToken.None);
    }

    [Fact]
    public async Task The_wire_request_itself_keeps_the_real_values()
    {
        var rendered = await RenderAsync();
        Assert.Equal("env-secret-value-123", rendered.Headers["X-Api-Token"]);
        Assert.Contains("provider-secret-value-9", rendered.Body);
        Assert.Equal("Bearer super-secret-token-value", rendered.Headers["Authorization"]);
    }

    [Fact]
    public async Task The_redactor_knows_the_cascade_secret()
    {
        var rendered = await RenderAsync();
        Assert.Equal(SecretRedactor.Mask, rendered.Redactor.Redact("env-secret-value-123"));
    }

    [Fact]
    public async Task The_redactor_knows_the_provider_secret()
    {
        var rendered = await RenderAsync();
        Assert.DoesNotContain("provider-secret-value-9", rendered.Redactor.Redact(rendered.Body));
    }

    [Fact]
    public async Task Redacted_headers_are_safe_to_echo()
    {
        var rendered = await RenderAsync();
        var safe = rendered.Redactor.RedactHeaders(rendered.Headers);
        Assert.Equal(SecretRedactor.Mask, safe["Authorization"]);
        Assert.Equal(SecretRedactor.Mask, safe["X-Api-Token"]);
        Assert.Equal("application/json", safe["Accept"]);
    }

    [Fact]
    public async Task Metadata_records_the_joined_base_url_but_never_a_value()
    {
        var rendered = await RenderAsync();
        Assert.Equal("http://api.demo.test", rendered.Metadata.ResolvedBaseUrl);
        Assert.All(rendered.Metadata.VariablesUsed, v =>
        {
            Assert.DoesNotContain("env-secret-value-123", v.Name);
            Assert.DoesNotContain("provider-secret-value-9", v.Name);
        });
    }

    [Fact]
    public async Task An_absolute_url_reports_no_base_join()
    {
        var absolute = Parse("collections/demo/abs.req.tap", """
            ---
            kind: request
            name: Absolute
            ---

            ```http
            GET http://elsewhere.test/status
            ```
            """);
        var ws = BuildWorkspace(DemoCollection(), BearerAuth, absolute);
        var renderer = new WorkspaceRenderer(ws, Registry());
        var rendered = await renderer.RenderAsync((RequestFile)absolute, null, null, CancellationToken.None);
        Assert.Null(rendered.Metadata.ResolvedBaseUrl);
        Assert.Equal("http://elsewhere.test/status", rendered.Url);
    }
}
