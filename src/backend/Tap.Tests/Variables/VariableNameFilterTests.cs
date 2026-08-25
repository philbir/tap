using Tap.Execution.Variables;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Tests.Variables;

/// <summary>
/// The provider name filter: what it admits, what it refuses, and — for the Key Vault
/// provider that uses it — that the refusal holds for lookups and writes, not just listings.
/// A filter that only hid rows would be a display trick, not a scope.
/// </summary>
public sealed class VariableNameFilterTests
{
    [Fact]
    public void A_blank_pattern_admits_everything()
    {
        foreach (var blank in new string?[] { null, "", "   " })
        {
            var filter = VariableNameFilter.Create(blank, "kv");
            Assert.True(filter.IsEmpty);
            Assert.True(filter.IsMatch("anything-at-all"));
        }
    }

    [Fact]
    public void The_pattern_is_unanchored_so_it_matches_anywhere_in_the_name()
    {
        var filter = VariableNameFilter.Create("billing", "kv");

        Assert.True(filter.IsMatch("billing-api-key"));
        Assert.True(filter.IsMatch("acme-billing-token"));
        Assert.False(filter.IsMatch("payments-key"));
    }

    [Fact]
    public void Anchors_do_what_anchors_do()
    {
        var filter = VariableNameFilter.Create("^billing-", "kv");

        Assert.True(filter.IsMatch("billing-api-key"));
        Assert.False(filter.IsMatch("acme-billing-token"));
    }

    [Fact]
    public void An_invalid_pattern_names_the_provider_and_the_pattern()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => VariableNameFilter.Create("^(unclosed", "kv-prod"));

        Assert.Equal(WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID, ex.Error.Code);
        Assert.Contains("kv-prod", ex.Error.Message, StringComparison.Ordinal);
        Assert.Contains("^(unclosed", ex.Error.Message, StringComparison.Ordinal);
    }

    // --- The Key Vault provider's use of it ------------------------------------------------
    //
    // These exercise the paths that answer before any network call, so they need no vault:
    // an out-of-scope name is decided locally, which is the whole point.

    private static AzureKeyVaultVariableProvider Vault(string filter) => new(
        new VariableProviderConfig
        {
            Name = "kv",
            Type = "azkv",
            Origin = ProviderOrigin.Workspace,
            Settings = new Dictionary<string, string?> { ["vaultName"] = "acme-test", ["filter"] = filter },
        });

    [Fact]
    public async Task An_out_of_scope_lookup_is_a_miss_not_a_vault_call()
    {
        var provider = Vault("^billing-");

        Assert.Null(await provider.GetAsync("payments-key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_out_of_scope_write_is_refused_rather_than_hidden()
    {
        var provider = Vault("^billing-");

        var ex = await Assert.ThrowsAsync<WorkspaceParseException>(async () =>
            await provider.SetAsync("payments-key", "s3cret", isSecret: true, TestContext.Current.CancellationToken));

        Assert.Equal(WorkspaceErrorCode.E_PROVIDER_NOT_WRITABLE, ex.Error.Code);
        Assert.Contains("payments-key", ex.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", ex.Error.Message, StringComparison.Ordinal);

        var delete = await Assert.ThrowsAsync<WorkspaceParseException>(async () =>
            await provider.DeleteAsync("payments-key", TestContext.Current.CancellationToken));
        Assert.Equal(WorkspaceErrorCode.E_PROVIDER_NOT_WRITABLE, delete.Error.Code);
    }

    [Fact]
    public void An_invalid_filter_fails_the_provider_at_construction()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => Vault("^(unclosed"));

        Assert.Equal(WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID, ex.Error.Code);
    }

    [Fact]
    public void The_filter_is_advertised_on_the_type_descriptor()
    {
        var field = new AzureKeyVaultVariableProviderFactory().Descriptor.Fields
            .SingleOrDefault(f => f.Key == "filter");

        Assert.NotNull(field);
        Assert.False(field!.Required);
        Assert.NotNull(field.Note);
    }
}
