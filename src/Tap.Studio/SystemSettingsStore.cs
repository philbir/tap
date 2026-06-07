using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Studio.Variables;
using Tap.Workspace.Variables;

namespace Tap.Studio;

/// <summary>
/// Persistent store for user-level Tap settings. The file lives at
/// <c>&lt;SystemDir&gt;/system.json</c> — <c>SystemDir</c> comes from the <c>TAP_SYSTEM_DIR</c>
/// env var, falling back to <c>~/.tap</c>. Two sections:
/// <list type="bullet">
///   <item><b>Providers</b> — system-scope variable provider configs. The Studio merges these
///     with workspace-level providers when building the resolution registry (workspace
///     providers shadow same-named system ones).</item>
///   <item><b>Variables</b> — flat name/value/secret entries exposed through the built-in
///     <c>system</c> variable provider (always registered).</item>
///  </list>
///
/// <para>The file is treated as user-private. Values (including those flagged secret) are
/// stored verbatim — relying on the user-profile directory permissions instead of at-rest
/// encryption. Per-provider mechanisms (e.g. the <c>file</c> provider's AES envelope) still
/// apply when those providers are configured.</para>
///
/// <para>On first construction the file is created if missing, seeded with whatever providers
/// were declared in <c>StudioOptions.SystemProviders</c> (appsettings). After that the file is
/// the source of truth; the appsettings entries are ignored.</para>
/// </summary>
public sealed class SystemSettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private SystemSettingsFile _state;

    public SystemSettingsStore(StudioOptions options)
    {
        Directory.CreateDirectory(options.SystemDir);
        _path = Path.Combine(options.SystemDir, "system.json");
        SystemDir = options.SystemDir;

        var loaded = Load(_path);
        if (loaded is null)
        {
            _state = new SystemSettingsFile
            {
                VariableProviders = [.. options.SystemProviders.Select(StoredProvider.From)],
                Variables = new Dictionary<string, StoredVariable>(StringComparer.Ordinal),
            };
            Persist();
        }
        else
        {
            _state = loaded;
        }
    }

    public string SystemDir { get; }

    /// <summary>Currently configured default writable variable provider, or <c>null</c> when
    /// none is set. Mirrors the workspace manifest's <c>defaultVariableProvider:</c> field —
    /// the registry uses this as the system-wide fallback when the active workspace doesn't
    /// declare its own.</summary>
    public string? DefaultVariableProvider
    {
        get { lock (_gate) return _state.DefaultVariableProvider; }
    }

    /// <summary>The persisted system-scope provider configs, including the implicit
    /// <c>system</c> provider that exposes <see cref="Variables"/>. Returned fresh each call
    /// so callers always see the latest on-disk state.</summary>
    public IReadOnlyList<VariableProviderConfig> GetProviderConfigs()
    {
        lock (_gate)
        {
            var list = new List<VariableProviderConfig>(_state.VariableProviders.Count + 1)
            {
                // Implicit, always-on provider. Kept first so its variables resolve before
                // any user-configured provider on unprefixed lookups.
                new VariableProviderConfig
                {
                    Name = SystemVariableProvider.ProviderName,
                    Type = SystemVariableProvider.ProviderType,
                    Settings = new Dictionary<string, string?>(),
                    Origin = ProviderOrigin.System,
                },
            };
            foreach (var p in _state.VariableProviders)
            {
                list.Add(new VariableProviderConfig
                {
                    Name = p.Name,
                    Type = p.Type,
                    Settings = p.Settings ?? new Dictionary<string, string?>(),
                    Origin = ProviderOrigin.System,
                });
            }
            return list;
        }
    }

    /// <summary>Snapshot of the stored variables — name to (value, isSecret).</summary>
    public IReadOnlyDictionary<string, StoredVariable> GetVariables()
    {
        lock (_gate)
        {
            return new Dictionary<string, StoredVariable>(_state.Variables, StringComparer.Ordinal);
        }
    }

    /// <summary>Replace the providers list and the default-provider pointer atomically. The
    /// implicit <c>system</c> provider is filtered out — callers can't (and don't need to)
    /// redeclare it. Pass <c>null</c> for <paramref name="defaultProviderName"/> to clear the
    /// default. Empty / whitespace strings are normalized to <c>null</c>.</summary>
    public void ReplaceProviders(string? defaultProviderName, IEnumerable<StoredProvider> providers)
    {
        lock (_gate)
        {
            var clean = providers
                .Where(p => !string.IsNullOrEmpty(p.Name)
                    && !string.IsNullOrEmpty(p.Type)
                    && !string.Equals(p.Name, SystemVariableProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p.Type, SystemVariableProvider.ProviderType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Reject duplicate provider names — would otherwise produce silently shadowed
            // entries the user can't tell apart in the UI.
            var dupes = clean.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (dupes.Count > 0)
                throw new InvalidOperationException($"Duplicate provider name(s): {string.Join(", ", dupes)}.");

            // Default-provider sanity: ignore if it doesn't match a declared provider. We
            // deliberately don't error — workspace-level defaults shadow this anyway, and a
            // stale string in the JSON shouldn't block a save.
            var normalizedDefault = string.IsNullOrWhiteSpace(defaultProviderName) ? null : defaultProviderName.Trim();
            if (normalizedDefault is not null
                && !clean.Any(p => string.Equals(p.Name, normalizedDefault, StringComparison.OrdinalIgnoreCase)))
            {
                normalizedDefault = null;
            }

            _state = _state with { DefaultVariableProvider = normalizedDefault, VariableProviders = clean };
            Persist();
        }
        Changed?.Invoke();
    }

    public void ReplaceVariables(IEnumerable<KeyValuePair<string, StoredVariable>> variables)
    {
        lock (_gate)
        {
            var dict = new Dictionary<string, StoredVariable>(StringComparer.Ordinal);
            foreach (var (k, v) in variables)
            {
                if (string.IsNullOrEmpty(k)) continue;
                dict[k] = v;
            }
            _state = _state with { Variables = dict };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Upsert one variable. Used by the system variable provider's <c>SetAsync</c>
    /// so request execution can write back to <c>system.json</c> via
    /// <c>{{system:NAME}}</c> targets.</summary>
    public void SetVariable(string name, string value, bool isSecret)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Variable name is required.", nameof(name));

        lock (_gate)
        {
            var dict = new Dictionary<string, StoredVariable>(_state.Variables, StringComparer.Ordinal)
            {
                [name] = new StoredVariable(value, isSecret),
            };
            _state = _state with { Variables = dict };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Fired after any successful write. The provider-registry rebuild path picks
    /// fresh state on every <c>CreateRegistry()</c> call anyway, but UI clients use this to
    /// invalidate the Settings page.</summary>
    public event Action? Changed;

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_state, SystemSettingsJson.Default.SystemSettingsFile));
    }

    private static SystemSettingsFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // Read into the transitional shape so older files using the legacy `providers`
            // key still load. The first subsequent save rewrites under the canonical
            // `variableProviders` key.
            var raw = JsonSerializer.Deserialize(File.ReadAllText(path), SystemSettingsJson.Default.SystemSettingsFileRaw);
            if (raw is null) return null;
            return new SystemSettingsFile
            {
                DefaultVariableProvider = string.IsNullOrWhiteSpace(raw.DefaultVariableProvider) ? null : raw.DefaultVariableProvider,
                VariableProviders = raw.VariableProviders ?? raw.Providers ?? [],
                Variables = raw.Variables ?? new Dictionary<string, StoredVariable>(StringComparer.Ordinal),
            };
        }
        catch
        {
            // Refuse to crash the host over a corrupt user-config file. The Settings page
            // shows the underlying dir so the user can fix or delete it.
            return null;
        }
    }
}

public sealed record SystemSettingsFile
{
    /// <summary>Name of the writable provider that receives writes when callers don't name
    /// one explicitly. Mirrors the workspace manifest's <c>defaultVariableProvider:</c>
    /// field. <c>null</c> means "no system default" — fall through to the first ReadWrite
    /// provider in registration order.</summary>
    public string? DefaultVariableProvider { get; init; }
    public required IReadOnlyList<StoredProvider> VariableProviders { get; init; }
    public required IReadOnlyDictionary<string, StoredVariable> Variables { get; init; }
}

/// <summary>Read-only shape used during deserialization to absorb files written by older
/// builds (legacy <c>providers:</c> key) without losing data. Never serialized — the
/// canonical <see cref="SystemSettingsFile"/> is what hits disk.</summary>
internal sealed record SystemSettingsFileRaw
{
    public string? DefaultVariableProvider { get; init; }
    public IReadOnlyList<StoredProvider>? VariableProviders { get; init; }
    /// <summary>Legacy key. Used only when <see cref="VariableProviders"/> is absent.</summary>
    public IReadOnlyList<StoredProvider>? Providers { get; init; }
    public IReadOnlyDictionary<string, StoredVariable>? Variables { get; init; }
}

public sealed record StoredProvider
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    public static StoredProvider From(VariableProviderConfig cfg) => new()
    {
        Name = cfg.Name,
        Type = cfg.Type,
        Settings = new Dictionary<string, string?>(cfg.Settings),
    };
}

public sealed record StoredVariable(string Value, bool Secret);

[JsonSerializable(typeof(SystemSettingsFile))]
[JsonSerializable(typeof(SystemSettingsFileRaw))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SystemSettingsJson : JsonSerializerContext
{
}
