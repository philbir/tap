using Tap.Execution.Agent;
using Tap.Workspace.Model;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Agent;

/// <summary>
/// The inventory is what an agent reads before it runs anything, so the tests pin the two
/// promises that matter: it describes everything without rendering anything, and no shape in
/// it ever carries auth material beyond name + type.
/// </summary>
public class WorkspaceInventoryTests
{
    private static readonly WorkspaceFile Manifest = Parse("workspace.tap", """
        ---
        kind: workspace
        name: Inventory WS
        defaultEnv: environments/dev.env.tap
        ---
        """);

    private static readonly WorkspaceFile DevEnv = Parse("environments/dev.env.tap", """
        ---
        kind: env
        name: Dev
        ---
        """);

    private static readonly WorkspaceFile ProdEnv = Parse("environments/prod.env.tap", """
        ---
        kind: env
        name: Prod
        ---
        """);

    private static readonly WorkspaceFile GetRequest = Parse("collections/demo/get.req.tap", """
        ---
        kind: request
        name: Get thing
        tags: [demo]
        assertions:
        - status: 200
        ---

        ```http
        GET /things/{{thing.id}}
        X-Trace: {{TRACE}}
        ```
        """);

    private static readonly WorkspaceFile PostRequest = Parse("collections/demo/post.req.tap", """
        ---
        kind: request
        name: Create thing
        ---

        ```http
        POST /things
        Content-Type: application/json

        {"name":"{{thing.name}}"}
        ```
        """);

    private static readonly WorkspaceFile LooseRequest = Parse("requests/loose.req.tap", """
        ---
        kind: request
        name: Loose
        ---

        ```http
        GET http://standalone.test/ping
        ```
        """);

    private static readonly TestSetFile Smoke = new()
    {
        Kind = WorkspaceKind.Test,
        RelativePath = "tests/smoke.test.tap",
        Name = "Smoke",
        Tests =
        [
            new TestEntry { Request = WorkspaceRef.FromPath("../collections/demo/get.req.tap") },
            new TestEntry { Request = WorkspaceRef.FromPath("../collections/demo/post.req.tap") },
        ],
    };

    private static readonly FlowFile Checkout = new()
    {
        Kind = WorkspaceKind.Flow,
        RelativePath = "tests/checkout.flow.tap",
        Name = "Checkout",
        Steps =
        [
            new FlowStep { Request = WorkspaceRef.FromPath("../collections/demo/get.req.tap") },
            new FlowStep { Request = WorkspaceRef.FromPath("../collections/demo/post.req.tap") },
            new FlowStep { Request = WorkspaceRef.FromPath("../collections/demo/get.req.tap") },
        ],
    };

    private static Tap.Workspace.LoadedWorkspace Ws()
        => BuildWorkspace(
            Manifest, DevEnv, ProdEnv,
            DemoCollection(baseUrl: "{{DEMO_API_URL}}"), BearerAuth, UatEnv,
            GetRequest, PostRequest, LooseRequest, Smoke, Checkout);

    [Fact]
    public void The_inventory_covers_every_kind()
    {
        var inventory = WorkspaceInventory.Build(Ws());

        Assert.Equal("Inventory WS", inventory.Name);
        Assert.Equal("environments/dev.env.tap", inventory.DefaultEnv);

        var demo = Assert.Single(inventory.Collections);
        Assert.Equal("Demo", demo.Name);
        Assert.Equal("{{DEMO_API_URL}}", demo.BaseUrl);
        // Only the scoped env is listed on the collection — the two globals apply everywhere
        // and are reported once, on inventory.Envs.
        Assert.Equal(["collections/demo/uat.env.tap"], demo.Environments);
        Assert.Equal(2, demo.RequestCount);

        Assert.Equal(3, inventory.Requests.Count);
        Assert.Equal(3, inventory.Envs.Count);
        Assert.Empty(inventory.Envs.Single(e => e.Name == "Dev").Collections);
        var uat = Assert.Single(inventory.Envs.Single(e => e.Name == "UAT").Collections);
        Assert.Equal("demo", uat.Collection);
        Assert.Equal("http://uat.demo.test", uat.BaseUrl);
        Assert.True(inventory.Envs.Single(e => e.Name == "Dev").IsDefault);
        Assert.False(inventory.Envs.Single(e => e.Name == "Prod").IsDefault);

        Assert.Collection(
            inventory.Tests.OrderBy(t => t.Kind),
            flow => { Assert.Equal("flow", flow.Kind); Assert.Equal(3, flow.EntryCount); },
            test => { Assert.Equal("test", test.Kind); Assert.Equal(2, test.EntryCount); });

        var auth = Assert.Single(inventory.Auths);
        Assert.Equal("Demo Bearer", auth.Name);
        Assert.Equal("bearer", auth.Type);
    }

    [Fact]
    public void A_collection_request_reports_its_effective_auth_by_name_only()
    {
        var inventory = WorkspaceInventory.Build(Ws());
        var get = inventory.Requests.Single(r => r.Path == "collections/demo/get.req.tap");

        Assert.Equal("collections/demo/_collection.tap", get.Collection);
        Assert.Equal("GET", get.Method);
        Assert.Equal("/things/{{thing.id}}", get.UrlTemplate);
        Assert.Equal("Demo Bearer", get.Auth);
        Assert.Equal("bearer", get.AuthType);
        Assert.Equal(1, get.AssertionCount);
    }

    [Fact]
    public void A_request_outside_any_collection_stands_alone()
    {
        var inventory = WorkspaceInventory.Build(Ws());
        var loose = inventory.Requests.Single(r => r.Path == "requests/loose.req.tap");

        Assert.Null(loose.Collection);
        Assert.Null(loose.Auth);
        Assert.Equal("http://standalone.test/ping", loose.UrlTemplate);
    }

    [Fact]
    public void Describe_lays_out_the_full_template_surface()
    {
        var ws = Ws();
        var described = WorkspaceInventory.Describe(ws, (RequestFile)GetRequest);

        Assert.Equal("GET", described.Method);
        Assert.Equal("/things/{{thing.id}}", described.UrlTemplate);
        var header = Assert.Single(described.Headers);
        Assert.Equal("X-Trace", header.Name);
        Assert.Equal("{{TRACE}}", header.Value);
        Assert.Null(described.BodyTemplate);

        // Tokens from the block and from the collection's baseUrl, since a caller overrides
        // either the same way.
        Assert.Equal(["DEMO_API_URL", "TRACE", "thing.id"], described.VariablesReferenced);

        Assert.Equal("Demo Bearer", described.Auth);
        // Every env the request could run under: both globals plus its collection's own.
        Assert.Equal(
            ["collections/demo/uat.env.tap", "environments/dev.env.tap", "environments/prod.env.tap"],
            described.Environments.Order(StringComparer.Ordinal));
        Assert.Equal("status = 200", Assert.Single(described.Assertions));
    }

    [Fact]
    public void Describe_includes_a_body_template_verbatim()
    {
        var described = WorkspaceInventory.Describe(Ws(), (RequestFile)PostRequest);
        Assert.Equal("""{"name":"{{thing.name}}"}""", described.BodyTemplate);
    }
}
