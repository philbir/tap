using Tap.Execution.Agent;
using Tap.Workspace.Model;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Agent;

/// <summary>
/// The collection-level <c>agent:</c> option: how it parses, and that every agent surface
/// honours it — the dynamic factory refuses, discovery omits, and the flag itself is
/// visible so an agent can explain the refusal instead of retrying.
/// </summary>
public class AgentAccessTests
{
    private static CollectionFile LockedCollection(string agentYaml) => (CollectionFile)Parse(
        "collections/locked/_collection.tap", $"""
        ---
        kind: collection
        name: Locked
        baseUrl: http://127.0.0.1:9
        {agentYaml}
        ---
        """);

    private static readonly WorkspaceFile LockedRequest = Parse("collections/locked/get.req.tap", """
        ---
        kind: request
        name: Locked get
        ---

        ```http
        GET /things
        ```
        """);

    [Theory]
    [InlineData("agent: false", false)]
    [InlineData("agent: true", true)]
    [InlineData("agent: { enabled: false }", false)]
    [InlineData("agent: { enabled: true }", true)]
    [InlineData("agent: {}", true)]
    [InlineData("", true)]
    public void The_agent_option_parses_in_both_forms(string yaml, bool enabled)
        => Assert.Equal(enabled, LockedCollection(yaml).Agent.Enabled);

    [Fact]
    public void An_invalid_agent_value_is_a_parse_error_not_a_silent_default()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => LockedCollection("agent: maybe"));
        Assert.Contains("agent", ex.Error.Message);
    }

    [Fact]
    public void The_dynamic_factory_refuses_a_disabled_collection()
    {
        var ws = BuildWorkspace(LockedCollection("agent: false"), LockedRequest);
        var ex = Assert.Throws<WorkspaceParseException>(() => DynamicRequestFactory.Create(
            ws, "Locked", new DynamicRequestSpec { Method = "GET", Url = "/x" }));
        Assert.Equal(WorkspaceErrorCode.E_AGENT_ACCESS_DISABLED, ex.Error.Code);
    }

    [Fact]
    public void Discovery_flags_the_collection_and_omits_its_requests()
    {
        var ws = BuildWorkspace(
            LockedCollection("agent: false"), LockedRequest,
            DemoCollection(), BearerAuth,
            Parse("collections/demo/open.req.tap", """
                ---
                kind: request
                name: Open get
                ---

                ```http
                GET /things
                ```
                """));

        var inventory = WorkspaceInventory.Build(ws);

        var locked = inventory.Collections.Single(c => c.Name == "Locked");
        Assert.False(locked.AgentEnabled);
        Assert.Equal(1, locked.RequestCount);

        Assert.True(inventory.Collections.Single(c => c.Name == "Demo").AgentEnabled);
        Assert.Equal(["collections/demo/open.req.tap"], inventory.Requests.Select(r => r.Path));
    }

    [Fact]
    public void The_option_survives_the_studio_emit_parse_round_trip()
    {
        // The Studio UI saves collections through the spec emitter; if the emitter dropped
        // the option, one edit in the editor would silently re-open the collection to agents.
        var disabled = Tap.Studio.Specs.CollectionSpecEmitter.ToFileSource(
            new Tap.Studio.Contracts.CollectionSpecDto { Slug = "locked", Name = "Locked", AgentEnabled = false });
        var parsed = (CollectionFile)Parse("collections/locked/_collection.tap", disabled);
        Assert.False(parsed.Agent.Enabled);

        var defaulted = Tap.Studio.Specs.CollectionSpecEmitter.ToFileSource(
            new Tap.Studio.Contracts.CollectionSpecDto { Slug = "open", Name = "Open" });
        Assert.DoesNotContain("agent", defaulted);
        Assert.True(((CollectionFile)Parse("collections/open/_collection.tap", defaulted)).Agent.Enabled);
    }

    [Fact]
    public void A_request_outside_any_collection_is_never_gated()
    {
        var loose = (RequestFile)Parse("requests/loose.req.tap", """
            ---
            kind: request
            name: Loose
            ---

            ```http
            GET http://standalone.test/ping
            ```
            """);
        var ws = BuildWorkspace(loose);
        Assert.True(AgentAccess.IsEnabled(ws, loose));
        AgentAccess.EnsureAllowed(ws, loose);
    }
}
