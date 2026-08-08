using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Studio.Variables;

/// <summary>
/// Read-only provider backed by Azure Key Vault. Authentication uses
/// <see cref="DefaultAzureCredential"/>, so it transparently picks up <c>az login</c>, managed
/// identities, environment variables, and IDE credentials — no client-secret config in the
/// workspace file.
///
/// <para>Settings consumed:</para>
/// <list type="bullet">
///   <item><c>vaultName</c> — required. The short vault name (e.g. <c>my-team-kv</c>);
///     the provider expands it to <c>https://&lt;name&gt;.vault.azure.net/</c>.</item>
///   <item><c>tenantId</c> — optional. Forwarded to <see cref="DefaultAzureCredentialOptions"/>
///     for tenant-pinned auth when the host has access to multiple tenants.</item>
///   <item><c>prefix</c> — optional. Prepended to every lookup name when calling KV, so a
///     workspace can scope to a folder of secrets without spelling out the prefix in every
///     <c>{{…}}</c> token. The variable's reported name (as seen by tokens) is the unprefixed
///     form; the actual KV key is <c>prefix + name</c>.</item>
/// </list>
///
/// <para>Every value from KV is treated as <see cref="VariableValue.IsSecret"/> = true. KV
/// values are sensitive by definition; we don't bother distinguishing.</para>
/// </summary>
public sealed class AzureKeyVaultVariableProvider : IVariableProvider, IRefreshableVariableProvider
{
    private readonly VariableProviderConfig _config;
    private readonly SecretClient _client;
    private readonly string _prefix;
    private readonly Lock _gate = new();
    private IReadOnlyList<VariableValue>? _listCache;

    public AzureKeyVaultVariableProvider(VariableProviderConfig config)
    {
        _config = config;

        if (!_config.Settings.TryGetValue("vaultName", out var vaultName) || string.IsNullOrEmpty(vaultName))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"Azure Key Vault provider '{config.Name}' is missing required setting 'vaultName'."));
        }

        var options = new DefaultAzureCredentialOptions();
        if (_config.Settings.TryGetValue("tenantId", out var tenant) && !string.IsNullOrEmpty(tenant))
        {
            options.TenantId = tenant;
        }

        var uri = new Uri($"https://{vaultName}.vault.azure.net/");
        _client = new SecretClient(uri, new DefaultAzureCredential(options));
        _prefix = _config.Settings.TryGetValue("prefix", out var p) ? (p ?? string.Empty) : string.Empty;
    }

    public string Name => _config.Name;
    public ProviderMode Mode => ProviderMode.ReadWrite;  // static — KV supports SetSecret, so writes go through.
    public VariableProviderConfig Config => _config;

    public async ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct)
    {
        var keyVaultName = _prefix + name;
        try
        {
            var response = await _client.GetSecretAsync(keyVaultName, cancellationToken: ct).ConfigureAwait(false);
            return new VariableValue(name, response.Value.Value, IsSecret: true, Name);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 400 && ex.ErrorCode == "BadParameter")
        {
            // KV secret names only allow [0-9a-zA-Z-]; anything else (DEMO_API_URL,
            // user.name, …) draws a 400 BadParameter. Such a name can never exist in a
            // vault, so it's a miss, not a provider failure — critical when a vault is the
            // env's default provider and every bare {{token}} gets probed here first.
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
    {
        // Listing KV secrets returns names + metadata only — values come from per-name
        // GetSecretAsync. The Value field on each listing is left empty; the UI shows the
        // name + "secret" badge and the registry resolves the value on demand when a token
        // references it. Cached for the provider's lifetime so one render hits KV once.
        lock (_gate)
        {
            if (_listCache is not null) return _listCache;
        }

        var list = new List<VariableValue>();
        await foreach (var prop in _client.GetPropertiesOfSecretsAsync(ct).ConfigureAwait(false))
        {
            if (prop.Enabled is false) continue;
            var kvName = prop.Name;
            if (_prefix.Length > 0 && !kvName.StartsWith(_prefix, StringComparison.Ordinal)) continue;
            var publicName = _prefix.Length > 0 ? kvName[_prefix.Length..] : kvName;
            list.Add(new VariableValue(publicName, string.Empty, IsSecret: true, Name));
        }

        lock (_gate) { _listCache = list; }
        return list;
    }

    public void InvalidateListCache()
    {
        lock (_gate) { _listCache = null; }
    }

    public async ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct)
    {
        // KV secrets are sensitive by definition — the isSecret flag is ignored here; values
        // are always stored as KV "secrets" regardless. Clear the list cache so the next
        // ListAsync reflects the new entry.
        _ = isSecret;
        var keyVaultName = _prefix + name;
        await _client.SetSecretAsync(keyVaultName, value, ct).ConfigureAwait(false);
        lock (_gate) { _listCache = null; }
    }
}

public sealed class AzureKeyVaultVariableProviderFactory : IVariableProviderFactory
{
    public string Type => "azkv";

    public ProviderTypeDescriptor Descriptor { get; } = new()
    {
        Type = "azkv",
        DisplayName = "Azure Key Vault",
        Icon = "azure",
        Description = "Reads and writes secrets in an Azure Key Vault via DefaultAzureCredential (az login, managed identity, …).",
        Mode = ProviderMode.ReadWrite,
        Fields =
        [
            new ProviderSettingField
            {
                Key = "vaultName",
                Label = "Vault name",
                Description = "Short vault name — expands to https://<name>.vault.azure.net/.",
                Required = true,
                Placeholder = "my-team-kv",
                Picker = "azure-keyvault",
            },
            new ProviderSettingField
            {
                Key = "tenantId",
                Label = "Tenant ID",
                Description = "Pins authentication to one tenant when your account can access several.",
            },
            new ProviderSettingField
            {
                Key = "prefix",
                Label = "Secret name prefix",
                Description = "Prepended to every Key Vault lookup; tokens keep using the unprefixed name.",
            },
        ],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new AzureKeyVaultVariableProvider(config);
}
