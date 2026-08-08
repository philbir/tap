using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Azure.Core;
using Azure.Identity;

namespace Tap.Studio.AzureDiscovery;

/// <summary>
/// Read-only Azure Resource Manager discovery for the Studio's Key Vault picker:
/// list the subscriptions the signed-in user can see, then the vaults inside one.
///
/// <para>Authentication is <b>Azure CLI only</b> (<see cref="AzureCliCredential"/>) — the
/// picker's contract is "whatever <c>az login</c> can see". The broader
/// <c>DefaultAzureCredential</c> chain is deliberately avoided here: its managed-identity /
/// IMDS probes can hang for a long time on developer machines, and a picker dialog needs a
/// fast yes-or-no. The provider itself keeps using DefaultAzureCredential at resolve time.</para>
///
/// <para>Calls ARM's REST API directly with a cached bearer token rather than pulling in the
/// Azure.ResourceManager package family — two GETs don't justify the dependency. All JSON
/// parsing is source-generated (<see cref="AzureArmJson"/>).</para>
/// </summary>
public sealed class AzureDiscoveryService
{
    private const string ArmBase = "https://management.azure.com";
    private static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AccessToken _token;

    /// <summary>Thrown when the Azure CLI credential can't produce a token. The message is
    /// user-facing — endpoints surface it verbatim in the picker dialog.</summary>
    public sealed class AzureDiscoveryException(string message, Exception? inner = null) : Exception(message, inner);

    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken ct)
    {
        var subs = new List<AzureSubscription>();
        var url = $"{ArmBase}/subscriptions?api-version=2022-12-01";
        while (url is not null)
        {
            var page = await GetAsync(url, AzureArmJson.Default.SubscriptionListPage, ct).ConfigureAwait(false);
            foreach (var s in page.Value ?? [])
            {
                if (s.SubscriptionId is null) continue;
                subs.Add(new AzureSubscription(
                    SubscriptionId: s.SubscriptionId,
                    DisplayName: s.DisplayName ?? s.SubscriptionId,
                    TenantId: s.TenantId,
                    State: s.State));
            }
            url = page.NextLink;
        }
        subs.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return subs;
    }

    public async Task<IReadOnlyList<AzureKeyVault>> ListKeyVaultsAsync(string subscriptionId, CancellationToken ct)
    {
        var vaults = new List<AzureKeyVault>();
        var url = $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}/providers/Microsoft.KeyVault/vaults?api-version=2022-07-01";
        while (url is not null)
        {
            var page = await GetAsync(url, AzureArmJson.Default.VaultListPage, ct).ConfigureAwait(false);
            foreach (var v in page.Value ?? [])
            {
                if (v.Name is null) continue;
                vaults.Add(new AzureKeyVault(
                    Name: v.Name,
                    ResourceGroup: ResourceGroupFromId(v.Id),
                    Location: v.Location));
            }
            url = page.NextLink;
        }
        vaults.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return vaults;
    }

    /// <summary>ARM resource ids look like
    /// <c>/subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.KeyVault/vaults/{name}</c>;
    /// the resource group is the segment after <c>resourceGroups</c>.</summary>
    private static string ResourceGroupFromId(string? id)
    {
        if (id is null) return string.Empty;
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }
        return string.Empty;
    }

    private async Task<T> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureDiscoveryException(
                $"Azure Resource Manager returned {(int)response.StatusCode}: {TryExtractArmError(body) ?? response.ReasonPhrase ?? "request failed"}");
        }
        return JsonSerializer.Deserialize(body, typeInfo)
            ?? throw new AzureDiscoveryException("Azure Resource Manager returned an empty response.");
    }

    private static string? TryExtractArmError(string body)
    {
        try
        {
            var err = JsonSerializer.Deserialize(body, AzureArmJson.Default.ArmErrorEnvelope);
            return err?.Error?.Message;
        }
        catch { return null; }
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // az's own token cache does the heavy lifting; this in-process cache just avoids
            // shelling out to `az account get-access-token` on every keystroke of the dialog.
            if (_token.Token is { Length: > 0 } && _token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
                return _token.Token;

            try
            {
                var credential = new AzureCliCredential();
                _token = await credential.GetTokenAsync(new TokenRequestContext(ArmScopes), ct).ConfigureAwait(false);
                return _token.Token;
            }
            catch (CredentialUnavailableException ex)
            {
                throw new AzureDiscoveryException(
                    "Azure CLI credential unavailable — install the Azure CLI and sign in with `az login`.", ex);
            }
            catch (AuthenticationFailedException ex)
            {
                throw new AzureDiscoveryException(
                    $"Azure CLI sign-in failed — run `az login` and retry. ({FirstLine(ex.Message)})", ex);
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOfAny(['\r', '\n']);
        return idx < 0 ? s : s[..idx];
    }
}

/// <summary>One subscription visible to the CLI credential.</summary>
public sealed record AzureSubscription(
    string SubscriptionId,
    string DisplayName,
    string? TenantId,
    string? State);

/// <summary>One Key Vault, with the fields the picker shows.</summary>
public sealed record AzureKeyVault(
    string Name,
    string ResourceGroup,
    string? Location);

// ---- ARM wire shapes (internal) ---------------------------------------------------------

internal sealed record SubscriptionListPage(
    IReadOnlyList<ArmSubscription>? Value,
    string? NextLink);

internal sealed record ArmSubscription(
    string? SubscriptionId,
    string? DisplayName,
    string? TenantId,
    string? State);

internal sealed record VaultListPage(
    IReadOnlyList<ArmVault>? Value,
    string? NextLink);

internal sealed record ArmVault(
    string? Id,
    string? Name,
    string? Location);

internal sealed record ArmErrorEnvelope(ArmError? Error);
internal sealed record ArmError(string? Code, string? Message);

[JsonSerializable(typeof(SubscriptionListPage))]
[JsonSerializable(typeof(VaultListPage))]
[JsonSerializable(typeof(ArmErrorEnvelope))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
internal partial class AzureArmJson : JsonSerializerContext
{
}
