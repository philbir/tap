using System.Text.Json;
using ModelContextProtocol;
using Tap.Studio.Cli.Mcp;
using Tap.Studio.Mcp;

namespace Tap.Tests.Agent;

/// <summary>
/// The MCP tool layer, exercised directly against a real workspace on disk — the transport
/// is the SDK's business, but what each tool returns (and refuses) is ours. Guard behaviour
/// matters most here: an absolute URL must be refused before render, and one smuggled in
/// through a variable must come back as a failed step, sent nowhere.
/// </summary>
public sealed class McpToolsTests : IDisposable
{
    private readonly string _root;
    private readonly TapStudioTools _tools;

    public McpToolsTests()
    {
        _root = Directory.CreateTempSubdirectory("tap-mcp-tests-").FullName;
        Write("workspace.tap", """
            ---
            kind: workspace
            name: MCP Test WS
            ---
            """);
        Write("collections/demo/_collection.tap", """
            ---
            kind: collection
            name: Demo
            baseUrl: http://127.0.0.1:9
            ---
            """);
        Write("collections/demo/get.req.tap", """
            ---
            kind: request
            name: Get thing
            assertions:
            - status: 200
            ---

            ```http
            GET /things/{{thing.id}}
            ```
            """);
        _tools = new TapStudioTools(new McpRuntime(_root, useCachedTokens: false));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Inventory_reflects_the_workspace_on_disk()
    {
        var inventory = Parse(_tools.WorkspaceInventory());
        Assert.Equal("MCP Test WS", inventory.GetProperty("name").GetString());
        Assert.Equal("Demo", inventory.GetProperty("collections")[0].GetProperty("name").GetString());
        Assert.Equal(1, inventory.GetProperty("requests").GetArrayLength());
    }

    [Fact]
    public void Describe_resolves_by_name_and_returns_the_template()
    {
        var described = Parse(_tools.DescribeRequest("Get thing"));
        Assert.Equal("/things/{{thing.id}}", described.GetProperty("urlTemplate").GetString());
        Assert.Equal("thing.id", described.GetProperty("variablesReferenced")[0].GetString());
    }

    [Fact]
    public void An_unknown_target_is_a_clean_protocol_error()
    {
        var ex = Assert.Throws<McpException>(() => _tools.DescribeRequest("no-such-request"));
        Assert.Contains("no-such-request", ex.Message);
    }

    [Fact]
    public async Task A_literal_absolute_url_is_refused_before_render()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CallRequest("Demo", "GET", "http://evil.example/x",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("relative", ex.Message);
    }

    [Fact]
    public async Task A_variable_absolute_url_comes_back_as_a_failed_unsent_step()
    {
        var step = Parse(await _tools.CallRequest(
            "Demo", "GET", "{{TARGET}}/x",
            vars: new Dictionary<string, string> { ["TARGET"] = "http://evil.example" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Contains("absolute URL", step.GetProperty("error").GetString());
        Assert.Equal(0, step.GetProperty("status").GetInt32());
        Assert.Equal(0, step.GetProperty("responseBodyBytes").GetInt64());
    }

    [Fact]
    public async Task An_agent_disabled_collection_refuses_describe_send_and_call()
    {
        Write("collections/locked/_collection.tap", """
            ---
            kind: collection
            name: Locked
            baseUrl: http://127.0.0.1:9
            agent: false
            ---
            """);
        Write("collections/locked/secret-op.req.tap", """
            ---
            kind: request
            name: Secret op
            ---

            ```http
            GET /admin
            ```
            """);

        Assert.Contains("agent access disabled",
            Assert.Throws<McpException>(() => _tools.DescribeRequest("Secret op")).Message);
        Assert.Contains("agent access disabled",
            (await Assert.ThrowsAsync<McpException>(() => _tools.SendRequest(
                "Secret op", cancellationToken: TestContext.Current.CancellationToken))).Message);
        Assert.Contains("agent access disabled",
            (await Assert.ThrowsAsync<McpException>(() => _tools.CallRequest(
                "Locked", "GET", "/x", cancellationToken: TestContext.Current.CancellationToken))).Message);

        // And the inventory shows the collection, flagged, without its request.
        var inventory = Parse(_tools.WorkspaceInventory());
        var locked = inventory.GetProperty("collections").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Locked");
        Assert.False(locked.GetProperty("agentEnabled").GetBoolean());
        Assert.DoesNotContain(
            inventory.GetProperty("requests").EnumerateArray(),
            r => r.GetProperty("path").GetString()!.StartsWith("collections/locked/"));
    }

    [Fact]
    public void The_tools_see_edits_made_after_the_server_started()
    {
        Write("collections/demo/post.req.tap", """
            ---
            kind: request
            name: Create thing
            ---

            ```http
            POST /things
            ```
            """);
        var inventory = Parse(_tools.WorkspaceInventory());
        Assert.Equal(2, inventory.GetProperty("requests").GetArrayLength());
    }
}
