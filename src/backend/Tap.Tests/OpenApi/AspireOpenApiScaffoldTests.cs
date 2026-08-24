using Microsoft.Extensions.Configuration;
using Tap.Studio;

namespace Tap.Tests.OpenApi;

/// <summary>
/// The Aspire hand-off: how the AppHost's API list reaches the Studio, and what the boot scaffold
/// writes once an API advertises an OpenAPI document.
/// </summary>
public class AspireOpenApiScaffoldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-aspire-openapi").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IConfiguration Config(string? apis)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:Aspire:Apis"] = apis })
            .Build();

    [Fact]
    public void The_api_list_carries_each_openapi_route()
    {
        var apis = AspireWorkspaceScaffold.ReadApis(Config(
            """[{"name":"orders-api","openApiRoute":"/openapi/v1.json"},{"name":"billing-api","openApiRoute":null}]"""));

        Assert.Equal(2, apis.Count);
        Assert.Equal("orders-api", apis[0].Name);
        Assert.Equal("/openapi/v1.json", apis[0].OpenApiRoute);
        Assert.Null(apis[1].OpenApiRoute);
    }

    /// <summary>An AppHost and a Studio can be different versions mid-upgrade. Failing to scaffold
    /// at all would be a much worse outcome than ignoring a route we don't have.</summary>
    [Fact]
    public void The_old_bare_name_array_is_still_understood()
    {
        var apis = AspireWorkspaceScaffold.ReadApis(Config("""["orders-api","billing-api"]"""));

        Assert.Equal(["orders-api", "billing-api"], apis.Select(a => a.Name));
        Assert.All(apis, a => Assert.Null(a.OpenApiRoute));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void A_malformed_api_list_scaffolds_nothing_rather_than_throwing(string? raw)
        => Assert.Empty(AspireWorkspaceScaffold.ReadApis(Config(raw)));

    /// <summary>The collection is still created up front — only the placeholder waits, because the
    /// post-startup scaffold is about to write real requests into it.</summary>
    [Fact]
    public void An_api_with_an_openapi_route_gets_a_collection_but_no_starter_request()
    {
        AspireWorkspaceScaffold.Run(_root, [
            new AspireWorkspaceScaffold.AspireApi("orders-api", "/openapi/v1.json"),
            new AspireWorkspaceScaffold.AspireApi("billing-api", null),
        ]);

        Assert.True(File.Exists(Path.Combine(_root, "collections", "orders-api", "_collection.tap")));
        Assert.False(File.Exists(Path.Combine(_root, "collections", "orders-api", "smoke.http")));

        // No route: unchanged behaviour, placeholder and all.
        Assert.True(File.Exists(Path.Combine(_root, "collections", "billing-api", "_collection.tap")));
        Assert.True(File.Exists(Path.Combine(_root, "collections", "billing-api", "smoke.http")));
    }

    /// <summary>The fallback when a fetch fails: the developer still gets something that sends.</summary>
    [Fact]
    public void The_starter_request_can_be_written_after_the_fact()
    {
        AspireWorkspaceScaffold.Run(_root, [new AspireWorkspaceScaffold.AspireApi("orders-api", "/openapi/v1.json")]);
        Assert.False(File.Exists(Path.Combine(_root, "collections", "orders-api", "smoke.http")));

        Assert.True(AspireWorkspaceScaffold.WriteStarterRequest(_root, "orders-api", "orders-api"));
        Assert.True(File.Exists(Path.Combine(_root, "collections", "orders-api", "smoke.http")));

        // Idempotent — a later failure must not stack a second placeholder.
        Assert.False(AspireWorkspaceScaffold.WriteStarterRequest(_root, "orders-api", "orders-api"));
    }

    /// <summary>Once real requests exist, a placeholder beside them is noise.</summary>
    [Fact]
    public void No_starter_is_written_when_the_collection_already_holds_requests()
    {
        AspireWorkspaceScaffold.Run(_root, [new AspireWorkspaceScaffold.AspireApi("orders-api", "/openapi/v1.json")]);
        var dir = Path.Combine(_root, "collections", "orders-api");
        File.WriteAllText(Path.Combine(dir, "orders.http"), "### Get\nGET /orders\n");

        Assert.False(AspireWorkspaceScaffold.WriteStarterRequest(_root, "orders-api", "orders-api"));
    }

    /// <summary>Scaffolding runs on every start, so it must never duplicate or clobber.</summary>
    [Fact]
    public void Running_twice_changes_nothing_the_second_time()
    {
        AspireWorkspaceScaffold.AspireApi[] apis = [new("orders-api", "/openapi/v1.json")];
        AspireWorkspaceScaffold.Run(_root, apis);
        var second = AspireWorkspaceScaffold.Run(_root, apis);

        Assert.True(second.IsNoOp);
    }
}
