using Tap.Workspace.Variables.Providers;

namespace Tap.Tests.Studio;

/// <summary>
/// <c>{{aspire:orders-api}}</c> resolution. The provider reads Aspire's *standard*
/// service-discovery environment variables rather than any Aspire API, which is what lets the
/// same workspace run under an AppHost, from the CLI, and in CI where someone exported those
/// variables by hand. These tests work against an injected environment map for that reason —
/// the production path reads the same shape from the process.
/// </summary>
public class AspireVariableProviderTests
{
    private static Dictionary<string, string> Env(params (string Key, string Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_resource_resolves_to_its_allocated_url()
    {
        var value = AspireVariableProvider.Resolve("orders-api",
            Env(("services__orders-api__http__0", "http://localhost:5231")));

        Assert.NotNull(value);
        Assert.Equal("http://localhost:5231", value.Value);
        Assert.False(value.IsSecret);
    }

    [Fact]
    public void Https_wins_over_http()
    {
        // A resource offering both is telling you which one it would rather you used.
        var value = AspireVariableProvider.Resolve("orders-api", Env(
            ("services__orders-api__http__0", "http://localhost:5231"),
            ("services__orders-api__https__0", "https://localhost:7231")));

        Assert.Equal("https://localhost:7231", value?.Value);
    }

    [Fact]
    public void The_lowest_index_wins_within_a_scheme()
    {
        var value = AspireVariableProvider.Resolve("orders-api", Env(
            ("services__orders-api__http__2", "http://localhost:3"),
            ("services__orders-api__http__0", "http://localhost:1"),
            ("services__orders-api__http__1", "http://localhost:2")));

        Assert.Equal("http://localhost:1", value?.Value);
    }

    [Fact]
    public void Resource_names_may_contain_dashes()
    {
        // The reason key matching is a pattern rather than a split on '__': Aspire resource
        // names are kebab-case far more often than not.
        var value = AspireVariableProvider.Resolve("my-orders-api-v2",
            Env(("services__my-orders-api-v2__https__0", "https://localhost:7000")));

        Assert.Equal("https://localhost:7000", value?.Value);
    }

    [Fact]
    public void A_trailing_slash_is_trimmed()
    {
        // The value is used as a baseUrl and joined with a relative request path; leaving the
        // slash on produces a double slash in every URL built from it.
        var value = AspireVariableProvider.Resolve("api", Env(("services__api__http__0", "http://localhost:5000/")));
        Assert.Equal("http://localhost:5000", value?.Value);
    }

    [Fact]
    public void An_unknown_resource_resolves_to_nothing()
    {
        // Null, not an exception: the registry turns it into E_PROVIDER_RESOLUTION_FAILED with
        // a message naming the variable it looked for.
        Assert.Null(AspireVariableProvider.Resolve("billing-api",
            Env(("services__orders-api__http__0", "http://localhost:5231"))));
    }

    [Fact]
    public void An_empty_value_is_not_treated_as_an_endpoint()
    {
        Assert.Null(AspireVariableProvider.Resolve("api", Env(("services__api__http__0", ""))));
    }

    [Fact]
    public void Every_advertised_resource_is_discoverable_for_the_vars_view()
    {
        var env = Env(
            ("services__orders-api__http__0", "http://localhost:1"),
            ("services__orders-api__https__0", "https://localhost:2"),
            ("services__billing__http__0", "http://localhost:3"),
            ("PATH", "/usr/bin"));

        var names = AspireVariableProvider.ResourceNames(env).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["billing", "orders-api"], names);
    }
}
