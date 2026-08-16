using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Execution.Variables;

/// <summary>
/// Read-only provider backed by 1Password via the <c>op</c> CLI. Like the Key Vault provider
/// it carries no credentials of its own by default: <c>op</c> is already authenticated on the
/// host (desktop-app integration / biometric unlock, or an existing <c>op signin</c>), so the
/// workspace file stays free of secrets.
///
/// <para><b>Reads only.</b> 1Password is treated as a source of truth that Tap consumes, not a
/// store it edits: secrets are created and rotated in 1Password itself, where the audit trail,
/// sharing rules, and item history live. That also sidesteps <c>op item edit</c>'s one real
/// hazard — it takes the value as a command-line argument, briefly exposing it to any process
/// that can read the host's process list.</para>
///
/// <para>Three shapes, selected by <c>mode</c>:</para>
/// <list type="number">
///   <item><b>Environment mode</b> — a 1Password Environment's variables are the provider's
///     variables. The closest match to what a variable provider actually is (a flat
///     name → value namespace), which is why it's the default.</item>
///   <item><b>Item mode</b> (<c>vault</c> + <c>item</c>) — one item's fields are the
///     variables. Fits a single "Acme API" item holding username + password + a custom
///     <c>api-key</c> field.</item>
///   <item><b>Vault mode</b> (<c>vault</c>) — every item in the vault is one variable, named
///     after the item title, valued by its <c>field</c> (default: its password/credential).
///     Mirrors the azkv model: vault → name → value.</item>
/// </list>
///
/// <para>Settings consumed:</para>
/// <list type="bullet">
///   <item><c>mode</c> — <c>environment</c> | <c>vault</c> | <c>item</c>. The explicit switch
///     the settings form writes, and what decides which of the settings below apply. A config
///     without it (hand-written YAML) falls back to inferring from whichever field is filled,
///     using the same precedence.</item>
///   <item><c>environment</c> — a 1Password Environment ID (copied from the app: Developer →
///     View Environments → Manage environment → Copy environment ID). The CLI offers no way
///     to list Environments, which is why this is an ID field rather than a picker. Needs
///     <c>op</c> <see cref="OnePasswordCli.EnvironmentsMinVersion"/> or later.</item>
///   <item><c>vault</c> — vault name or ID. Required unless <c>environment</c> is set.</item>
///   <item><c>item</c> — optional. Switches to item mode; name or ID.</item>
///   <item><c>field</c> — optional, <b>vault mode only</b>. The field read from each item.
///     Unset means "the item's password": first a field labelled <c>password</c>, then
///     <c>credential</c>, then any populated concealed field. Item mode ignores it — there the
///     variable name is already the field name.</item>
///   <item><c>account</c> — optional. Forwarded as <c>--account</c> when the host is signed in
///     to more than one 1Password account.</item>
///   <item><c>serviceAccountToken</c> — optional. Sets <c>OP_SERVICE_ACCOUNT_TOKEN</c> for the
///     spawned process — the headless/CI path. Ambient <c>OP_CONNECT_HOST</c> +
///     <c>OP_CONNECT_TOKEN</c> take precedence over it inside <c>op</c> itself.</item>
///   <item><c>cliPath</c> — optional. Explicit path to <c>op</c>; otherwise located via
///     <c>TAP_OP_CLI</c>, well-known install paths, then PATH.</item>
/// </list>
/// </summary>
public sealed class OnePasswordVariableProvider : IVariableProvider, IRefreshableVariableProvider
{
    public const string ProviderType = "1password";

    // Generous enough to absorb a biometric-unlock prompt on the desktop integration, tight
    // enough that a wedged CLI doesn't hang a render. The provider test applies its own 20s
    // cap on top.
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GetTimeout = TimeSpan.FromSeconds(20);

    private enum OpMode { Environment, Item, Vault }

    private readonly VariableProviderConfig _config;
    private readonly LocatedCli _cli;
    private readonly OpMode _mode;
    private readonly string? _environment;
    private readonly string? _vault;
    private readonly string? _item;
    private readonly string? _field;
    private readonly string? _account;
    private readonly string? _serviceAccountToken;

    private readonly Lock _gate = new();
    private IReadOnlyList<VariableValue>? _listCache;

    public OnePasswordVariableProvider(VariableProviderConfig config)
    {
        _config = config;

        _environment = Setting("environment");
        _vault = Setting("vault");
        _item = Setting("item");
        _field = Setting("field");
        _account = Setting("account");
        _serviceAccountToken = Setting("serviceAccountToken");

        // Environment first: it is the mode that maps one-to-one onto a variable namespace,
        // so a filled Environment ID is an unambiguous statement of intent even when leftover
        // vault/item settings are still sitting in the config.
        // `mode` is the explicit switch the settings form writes. A config without one — hand
        // written YAML — still works: fall back to inferring from whichever field is filled,
        // preferring environment, exactly the precedence the form presents.
        _mode = Setting("mode")?.ToLowerInvariant() switch
        {
            "environment" => OpMode.Environment,
            "item" => OpMode.Item,
            "vault" => OpMode.Vault,
            _ => _environment is not null ? OpMode.Environment
                : _item is not null ? OpMode.Item
                : OpMode.Vault,
        };

        // Which settings are mandatory depends on the mode, so the descriptor can't express it
        // with a Required flag — it only knows how to hide the fields a mode doesn't use. The
        // real rule lives here, where it can name the mode that's missing something.
        var missing = _mode switch
        {
            OpMode.Environment when _environment is null => "'environment' (a 1Password Environment ID)",
            OpMode.Item when _vault is null => "'vault'",
            OpMode.Item when _item is null => "'item'",
            OpMode.Vault when _vault is null => "'vault'",
            _ => null,
        };
        if (missing is not null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"1Password provider '{config.Name}' is in {_mode.ToString().ToLowerInvariant()} mode " +
                $"and needs {missing}."));
        }

        // A missing binary is deliberately NOT a constructor failure: the provider still
        // registers, and every call surfaces one actionable "install op" message instead of
        // the whole workspace refusing to load over a tool that isn't there yet.
        _cli = OnePasswordCli.Locate(Setting("cliPath"));

        string? Setting(string key) =>
            config.Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    }

    public string Name => _config.Name;
    public VariableProviderConfig Config => _config;

    public ProviderMode Mode => ProviderMode.Read;  // static — 1Password is a source here, never a sink.

    public async ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
    {
        // Cached for the instance's lifetime like azkv — one variables-panel render would
        // otherwise spawn `op` on every provider pass. The Studio's browse endpoint calls
        // InvalidateListCache() on an explicit refresh.
        lock (_gate)
        {
            if (_listCache is not null) return _listCache;
        }

        var list = _mode switch
        {
            OpMode.Environment => await ListEnvironmentAsync(ct).ConfigureAwait(false),
            OpMode.Item => ItemFields(
                    await GetItemAsync(_item!, ListTimeout, ct).ConfigureAwait(false)
                    ?? throw Failed($"item '{_item}' was not found in vault '{_vault}'."))
                .Select(f => new VariableValue(f.Name, f.IsSecret ? string.Empty : f.Value, f.IsSecret, Name))
                .ToList(),
            _ => await ListVaultItemsAsync(ct).ConfigureAwait(false),
        };

        lock (_gate) { _listCache = list; }
        return list;
    }

    private async Task<List<VariableValue>> ListVaultItemsAsync(CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(
            ["item", "list", "--vault", _vault!, "--format", "json"], ListTimeout, ct).ConfigureAwait(false);
        if (code != 0) throw Failed(OnePasswordCli.Explain(code, stderr, stdout));

        var items = OnePasswordCli.Deserialize(stdout, OnePasswordJson.Default.OnePasswordItemArray) ?? [];

        // Titles are not unique within a vault. A duplicate would make `op item get <title>`
        // ambiguous anyway, so collapse to one entry and let the read report the conflict.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<VariableValue>(items.Length);
        foreach (var item in items)
        {
            if (item.Title is not { Length: > 0 } title) continue;
            if (!seen.Add(title)) continue;
            // Values stay unfetched — listing an item does not read it. Assume secret.
            list.Add(new VariableValue(title, string.Empty, IsSecret: true, Name));
        }
        return list;
    }

    // ---- environment mode ---------------------------------------------------------------

    /// <summary>Reads a 1Password Environment. <c>op environment read</c> emits dotenv
    /// (<c>KEY=value</c> per line) rather than JSON, so the whole namespace arrives in one
    /// spawn — no per-name fetch, and <see cref="GetAsync"/> just serves from this list.</summary>
    private async Task<List<VariableValue>> ListEnvironmentAsync(CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(
            ["environment", "read", _environment!], ListTimeout, ct).ConfigureAwait(false);

        if (code != 0)
        {
            // The overwhelmingly likely cause on a stable build, and the one whose default
            // message ("unknown command") tells the user nothing about what to do.
            if (OnePasswordCli.IsUnknownCommand(stderr, "environment"))
            {
                throw Failed(
                    $"this 'op' build has no 'environment' command. 1Password Environments need CLI " +
                    $"{OnePasswordCli.EnvironmentsMinVersion} or later (beta channel). Use a vault instead, " +
                    "or install the beta build.");
            }
            throw Failed(OnePasswordCli.Explain(code, stderr, stdout));
        }

        return ParseDotEnv(stdout)
            // Every value is reported secret: `op environment read` gives no signal about the
            // per-variable "Hide value by default" flag, and guessing wrong would print a
            // secret into the variables panel. Masking a hostname is the cheaper mistake.
            .Select(kv => new VariableValue(kv.Key, kv.Value, IsSecret: true, Name))
            .ToList();
    }

    /// <summary>Minimal dotenv reader for <c>op environment read</c> output: <c>KEY=value</c>
    /// per line, <c>#</c> comments and blanks skipped, one layer of matching surrounding
    /// quotes removed. Values containing literal newlines are not representable in this
    /// format and arrive truncated at the newline — a CLI limitation, not one we can fix.</summary>
    internal static List<KeyValuePair<string, string>> ParseDotEnv(string stdout)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#') continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal)) key = key["export ".Length..].Trim();
            if (key.Length == 0) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2
                && (value[0] == '"' || value[0] == '\'')
                && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            result.Add(new KeyValuePair<string, string>(key, value));
        }
        return result;
    }

    // ---- reads --------------------------------------------------------------------------

    public async ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct)
    {
        if (_mode == OpMode.Environment)
        {
            // One `op environment read` already carries every value, so resolving a name is a
            // lookup over the cached list rather than another spawn.
            var all = await ListAsync(ct).ConfigureAwait(false);
            return all.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Both item modes read one whole item and pick the field in-process. `op item get
        // --fields` would fetch less, but its output shape shifts between a single field and
        // several; one code path over one documented shape is worth the extra bytes.
        var (itemRef, fieldName) = _mode == OpMode.Item ? (_item!, name) : (name, _field);

        if (await GetItemAsync(itemRef, GetTimeout, ct).ConfigureAwait(false) is not { } item) return null;

        var fields = ItemFields(item);
        var hit = _mode == OpMode.Item
            ? fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            : PickField(fields, fieldName);

        return hit is null ? null : new VariableValue(name, hit.Value, hit.IsSecret, Name);
    }

    /// <summary>Fetches one item in full. Returns null when 1Password reports it doesn't exist
    /// — the miss that keeps a bare <c>{{token}}</c> falling through to the next provider. Any
    /// other non-zero exit (bad vault, not signed in, ambiguous title) throws, because
    /// silently treating those as "absent" is how a typo becomes a mystery.</summary>
    private async Task<OnePasswordItem?> GetItemAsync(string itemRef, TimeSpan timeout, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(
            ["item", "get", itemRef, "--vault", _vault!, "--format", "json", "--reveal"], timeout, ct)
            .ConfigureAwait(false);

        if (code != 0) return OnePasswordCli.IsItemMiss(stderr) ? null : throw Failed(OnePasswordCli.Explain(code, stderr, stdout));
        return OnePasswordCli.Deserialize(stdout, OnePasswordJson.Default.OnePasswordItem);
    }

    // ---- writes -------------------------------------------------------------------------

    public ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct)
        => throw new NotSupportedException(
            $"1Password provider '{Name}' is read-only. Edit the item or Environment in the " +
            "1Password app, or point the write at a read/write provider.");

    public void InvalidateListCache()
    {
        lock (_gate) { _listCache = null; }
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await OnePasswordCli
                .RunAsync(_cli, args, _account, _serviceAccountToken, timeout, ct)
                .ConfigureAwait(false);
        }
        catch (OnePasswordCliException ex)
        {
            throw Failed(ex.Message);
        }
    }

    // ---- field shaping ------------------------------------------------------------------

    private sealed record Field(string Name, string Value, bool IsSecret);

    /// <summary>Flattens an item's fields into name/value/secret triples. Section-scoped
    /// fields are named <c>section.field</c> — the same spelling <c>op</c>'s assignment syntax
    /// uses, so a name that reads here also writes there, and two same-labelled fields in
    /// different sections stay distinct. Fields with no value are dropped: they can't resolve
    /// to anything, and they'd pad every listing with blanks.</summary>
    private static List<Field> ItemFields(OnePasswordItem item)
    {
        var fields = new List<Field>();
        foreach (var f in item.Fields ?? [])
        {
            if (f.Value is not { Length: > 0 } value) continue;

            var label = f.Label is { Length: > 0 } l ? l : f.Id;
            if (label is not { Length: > 0 }) continue;

            var name = f.Section?.Label is { Length: > 0 } section ? $"{section}.{label}" : label;
            fields.Add(new Field(name, value, IsSecretField(f)));
        }
        return fields;
    }

    private static bool IsSecretField(OnePasswordField f) =>
        string.Equals(f.Type, "CONCEALED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(f.Purpose, "PASSWORD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vault mode's "which field is the value?" rule. An explicit <c>field</c> is
    /// matched by name and nothing else. Without one, walk the conventions in order — a
    /// Login's password, an API Credential's credential, then any populated concealed field —
    /// so a vault of mixed item categories resolves without per-item configuration.</summary>
    private static Field? PickField(List<Field> fields, string? preferred)
    {
        if (preferred is { Length: > 0 })
            return fields.FirstOrDefault(f => string.Equals(f.Name, preferred, StringComparison.OrdinalIgnoreCase));

        return fields.FirstOrDefault(f => string.Equals(f.Name, "password", StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => string.Equals(f.Name, "credential", StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => f.IsSecret);
    }

    private WorkspaceParseException Failed(string detail) => new(new WorkspaceError(
        WorkspaceErrorCode.E_PROVIDER_RESOLUTION_FAILED,
        $"1Password provider '{Name}': {detail}"));
}

public sealed class OnePasswordVariableProviderFactory : IVariableProviderFactory
{
    public string Type => OnePasswordVariableProvider.ProviderType;

    public ProviderTypeDescriptor Descriptor { get; } = new()
    {
        Type = OnePasswordVariableProvider.ProviderType,
        DisplayName = "1Password",
        Icon = "1password",
        Description = "Reads a 1Password Environment, vault, or item through the local `op` CLI — no credentials stored in the workspace.",
        Mode = ProviderMode.Read,
        Fields =
        [
            // First, because nothing else works without it: the provider is a wrapper around a
            // binary that has to be present and signed in. Detect answers "is this going to
            // work at all?" before the user invests in filling out a mode.
            new ProviderSettingField
            {
                Key = "cliPath",
                Label = "op CLI path",
                Description = "Leave empty to auto-detect: TAP_OP_CLI, then the usual install locations, then PATH. "
                    + "System-scope providers only — a workspace file cannot choose which binary Tap runs.",
                Picker = "1password-cli",
                // This setting names an executable, and workspace.tap travels with a cloned repo.
                SystemScopeOnly = true,
                Note = new ProviderFieldNote
                {
                    Text = "This provider shells out to the 1Password CLI, so `op` must be installed on this machine.",
                    Url = OnePasswordCli.DownloadUrl,
                    UrlLabel = "Install the 1Password CLI",
                },
            },
            new ProviderSettingField
            {
                Key = "mode",
                Label = "Mode",
                Description = "What this provider's variables come from.",
                Kind = ProviderFieldKind.Select,
                DefaultValue = "environment",
                Options =
                [
                    new ProviderFieldOption
                    {
                        Value = "environment",
                        Label = "Environment",
                        Description = "A 1Password Environment's variables. Read-only.",
                    },
                    new ProviderFieldOption
                    {
                        Value = "vault",
                        Label = "Vault",
                        Description = "Every item in a vault, named after the item title.",
                    },
                    new ProviderFieldOption
                    {
                        Value = "item",
                        Label = "Item",
                        Description = "One item's fields, named after the field labels.",
                    },
                ],
            },
            new ProviderSettingField
            {
                Key = "environment",
                Label = "Environment ID",
                Description = "Copy it from 1Password: Developer → View Environments → Manage environment → Copy environment ID.",
                Placeholder = "(paste an Environment ID)",
                VisibleWhen = new ProviderFieldVisibility { Key = "mode", Values = ["environment"] },
                Note = new ProviderFieldNote
                {
                    Text = $"Environments are a beta feature: they need 1Password CLI {OnePasswordCli.EnvironmentsMinVersion} or later, "
                        + "which ships on the beta channel only.",
                    Url = OnePasswordCli.BetaDownloadUrl,
                    UrlLabel = "Download the beta CLI",
                },
            },
            new ProviderSettingField
            {
                Key = "vault",
                Label = "Vault",
                // Not flagged Required: the mode decides whether a vault is needed at all, and
                // the descriptor has no way to say "required in these modes". The provider
                // enforces the real rule and names the mode when it fails.
                Description = "Vault name or ID. Every lookup is scoped to it.",
                Placeholder = "Development",
                Picker = "1password-vault",
                VisibleWhen = new ProviderFieldVisibility { Key = "mode", Values = ["vault", "item"] },
            },
            new ProviderSettingField
            {
                Key = "item",
                Label = "Item",
                Description = "The item whose fields become this provider's variables.",
                Placeholder = "Acme API",
                VisibleWhen = new ProviderFieldVisibility { Key = "mode", Values = ["item"] },
            },
            new ProviderSettingField
            {
                Key = "field",
                Label = "Field",
                Description = "Which field of each item holds the value. Empty falls back to the item's password, then credential, then its first concealed field.",
                Placeholder = "password",
                // Vault mode only. In item mode the variable name *is* the field name, so a
                // separate field setting would have nothing to do.
                VisibleWhen = new ProviderFieldVisibility { Key = "mode", Values = ["vault"] },
            },
            new ProviderSettingField
            {
                Key = "account",
                Label = "Account",
                Description = "Account shorthand or sign-in address — only needed when op is signed in to several.",
            },
            new ProviderSettingField
            {
                Key = "serviceAccountToken",
                Label = "Service account token",
                Description = "For headless hosts. Empty uses whatever session op already has (desktop app integration, op signin).",
                Kind = ProviderFieldKind.Secret,
            },
        ],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new OnePasswordVariableProvider(config);
}
