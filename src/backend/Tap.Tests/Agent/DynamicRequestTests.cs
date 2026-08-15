using Tap.Execution.Agent;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Agent;

/// <summary>
/// A dynamic request is an agent's ad-hoc call carried by an existing collection. Two things
/// have to hold at once: it behaves exactly like a saved request (same parser, same cascade,
/// same auth), and it cannot carry the collection's credentials anywhere but the collection's
/// own host unless the caller explicitly says so.
/// </summary>
public class DynamicRequestTests
{
    private static DynamicRequestSpec Spec(string url = "/users/1", string method = "GET") => new()
    {
        Method = method,
        Url = url,
    };

    // ---- synthesis --------------------------------------------------------------------

    [Fact]
    public void Synthesizes_a_request_inside_the_collection()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var request = DynamicRequestFactory.Create(ws, "Demo", Spec());
        Assert.Equal("collections/demo/_dynamic.req.md", request.RelativePath);
        Assert.Equal("GET /users/1", request.Name);
        Assert.Contains("GET /users/1", request.HttpBlock);
    }

    [Fact]
    public void The_collection_resolves_by_name_directory_or_path()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        foreach (var key in new[] { "Demo", "demo", "collections/demo", "collections/demo/_collection.md" })
        {
            Assert.Equal(
                "collections/demo/_dynamic.req.md",
                DynamicRequestFactory.Create(ws, key, Spec()).RelativePath);
        }
    }

    [Fact]
    public void An_unknown_collection_names_the_available_ones()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var ex = Assert.Throws<WorkspaceParseException>(() => DynamicRequestFactory.Create(ws, "nope", Spec()));
        Assert.Equal(WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID, ex.Error.Code);
        Assert.Contains("Demo", ex.Error.Message);
    }

    [Fact]
    public void An_ambiguous_collection_name_is_refused()
    {
        var other = Parse("collections/demo2/_collection.md", """
            ---
            kind: collection
            name: Demo
            baseUrl: http://two.test
            ---
            """);
        var ws = BuildWorkspace(DemoCollection(), other, BearerAuth);
        var ex = Assert.Throws<WorkspaceParseException>(() => DynamicRequestFactory.Create(ws, "Demo", Spec()));
        Assert.Equal(WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID, ex.Error.Code);
    }

    [Fact]
    public void The_synthetic_path_never_shadows_a_real_file()
    {
        var occupant = Parse("collections/demo/_dynamic.req.md", """
            ---
            kind: request
            name: Occupied
            ---

            ```http
            GET /occupied
            ```
            """);
        var ws = BuildWorkspace(DemoCollection(), BearerAuth, occupant);
        Assert.Equal(
            "collections/demo/_dynamic-2.req.md",
            DynamicRequestFactory.Create(ws, "Demo", Spec()).RelativePath);
    }

    [Fact]
    public void A_body_containing_a_code_fence_survives()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var spec = Spec(method: "POST") with { Body = "text with\n```\na fence inside\n```" };
        var request = DynamicRequestFactory.Create(ws, "Demo", spec);
        Assert.Contains("a fence inside", request.HttpBlock);
        Assert.Contains("```", request.HttpBlock);
    }

    [Fact]
    public void An_auth_ref_is_carried_through()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var request = DynamicRequestFactory.Create(ws, "Demo", Spec() with { Auth = "./bearer.auth.md" });
        Assert.Equal("./bearer.auth.md", request.Auth?.SourceText);
    }

    [Fact]
    public void Multiline_headers_and_methods_are_refused()
    {
        // A newline in any part would smuggle extra lines into the synthesized http block —
        // a second header, or a body the caller never declared.
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        foreach (var bad in new[]
        {
            Spec() with { Method = "GET\nX-Evil: 1" },
            Spec() with { Url = "/a\n/b" },
            Spec() with { Headers = [new("X-Ok", "1\n\nsmuggled body")] },
            Spec() with { Headers = [new("X-Evil\nX: 1", "v")] },
            Spec() with { Method = " " },
        })
        {
            var ex = Assert.Throws<WorkspaceParseException>(() => DynamicRequestFactory.Create(ws, "Demo", bad));
            Assert.Equal(WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID, ex.Error.Code);
        }
    }

    // ---- rendering: full collection inheritance ---------------------------------------

    [Fact]
    public async Task Renders_with_the_collection_base_url_headers_and_auth()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var spec = Spec() with { Headers = [new("X-Extra", "1")] };
        var request = DynamicRequestFactory.Create(ws, "Demo", spec);

        var rendered = await new WorkspaceRenderer(ws, Registry())
            .RenderAsync(request, null, null, CancellationToken.None);

        Assert.Equal("http://api.demo.test/users/1", rendered.Url);
        Assert.Equal("application/json", rendered.Headers["Accept"]);
        Assert.Equal("1", rendered.Headers["X-Extra"]);
        Assert.Equal("Bearer super-secret-token-value", rendered.Headers["Authorization"]);

        DynamicRequestFactory.EnsureCollectionScoped(rendered, allowAnyUrl: false);
        Assert.Equal(SecretRedactor.Mask, rendered.Redactor.RedactHeaders(rendered.Headers)["Authorization"]);
    }

    // ---- the URL guard ----------------------------------------------------------------

    [Theory]
    [InlineData("http://evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("wss://evil.example/x")]
    public void A_literal_absolute_url_is_refused_up_front(string url)
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var ex = Assert.Throws<WorkspaceParseException>(() => DynamicRequestFactory.Create(ws, "Demo", Spec(url)));
        Assert.Equal(WorkspaceErrorCode.E_DYNAMIC_URL_NOT_COLLECTION_SCOPED, ex.Error.Code);
    }

    [Fact]
    public async Task A_variable_that_expands_to_an_absolute_url_is_caught_after_render()
    {
        // "{{TARGET}}/x" looks relative, so it passes the up-front check — but once the
        // variable expands, the renderer skips the baseUrl join and the request would carry
        // the collection's credentials to whatever host the variable named.
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var request = DynamicRequestFactory.Create(ws, "Demo", Spec("{{TARGET}}/x"));

        var rendered = await new WorkspaceRenderer(ws, Registry()).RenderAsync(
            request, null, new Dictionary<string, string> { ["TARGET"] = "http://evil.example" },
            CancellationToken.None);

        Assert.Equal("http://evil.example/x", rendered.Url);
        var ex = Assert.Throws<WorkspaceParseException>(
            () => DynamicRequestFactory.EnsureCollectionScoped(rendered, allowAnyUrl: false));
        Assert.Equal(WorkspaceErrorCode.E_DYNAMIC_URL_NOT_COLLECTION_SCOPED, ex.Error.Code);
    }

    [Fact]
    public async Task Allow_any_url_opts_out_of_both_checks()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var spec = Spec("http://elsewhere.test/status") with { AllowAnyUrl = true };
        var request = DynamicRequestFactory.Create(ws, "Demo", spec);

        var rendered = await new WorkspaceRenderer(ws, Registry())
            .RenderAsync(request, null, null, CancellationToken.None);

        Assert.Equal("http://elsewhere.test/status", rendered.Url);
        DynamicRequestFactory.EnsureCollectionScoped(rendered, allowAnyUrl: true);
    }
}
