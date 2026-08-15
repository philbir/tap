using System.Diagnostics;
using Tap.Workspace.Model;

namespace Tap.Workspace.Variables;

/// <summary>
/// Composes configured providers into a single resolver. Tracks a per-instance resolution
/// trace so executions can record which providers were consulted.
///
/// <para>Resolution rules:</para>
/// <list type="bullet">
///   <item><c>{{provider:name}}</c> — explicit. Resolves only against <c>provider</c>; fails
///     if absent. Always honours <see cref="VariableValue.IsSecret"/> for masking.</item>
///   <item><c>{{name}}</c> — when a <see cref="DefaultProviderName"/> is configured, that
///     provider is consulted first; remaining providers are then walked in registration
///     order. The first non-null hit wins. Without a default, the walk is purely
///     registration-order.</item>
/// </list>
///
/// <para>Cascade overrides (workspace/api/env/request file vars) are applied by the
/// renderer as a layer atop this registry — see <see cref="Rendering.VariableViewBuilder"/>.
/// The registry itself is provider-only.</para>
///
/// <para>"Overwrite by name" — when multiple providers expose the same name, the unprefixed
/// lookup returns the first checked provider's value (default first, then registration
/// order). The merged-view assembler dedupes by name using last-write-wins; callers that
/// need provenance can inspect each provider's <see cref="IVariableProvider.ListAsync"/>
/// directly.</para>
/// </summary>
public sealed class VariableProviderRegistry
{
    private readonly List<IVariableProvider> _providers;
    private readonly Dictionary<string, IVariableProvider> _byName;
    private readonly Dictionary<string, string>? _aliases;
    private readonly string? _defaultProviderName;
    private readonly bool _strict;
    private readonly List<VariableResolution> _trace = [];
    private readonly Dictionary<string, VariableValue?> _cache = new(StringComparer.Ordinal);

    public VariableProviderRegistry(
        IEnumerable<IVariableProvider> providers,
        string? defaultProviderName,
        IReadOnlyDictionary<string, string>? aliases = null,
        bool strict = false)
    {
        // Two `variableProviders` entries with the same name used to make ToDictionary throw out of
        // this constructor, and the workspace that declared them comes from a cloned repo. Keep the
        // first registration, drop the rest so the walk order stays unambiguous, and report the
        // collision instead of failing the load.
        var deduped = new List<IVariableProvider>();
        var byName = new Dictionary<string, IVariableProvider>(StringComparer.OrdinalIgnoreCase);
        var configErrors = new List<WorkspaceError>();
        foreach (var provider in providers)
        {
            if (!byName.TryAdd(provider.Name, provider))
            {
                configErrors.Add(new WorkspaceError(
                    WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                    $"More than one variable provider is named '{provider.Name}' (names are matched case-insensitively). " +
                    "The first registration wins; the rest are ignored."));
                continue;
            }
            deduped.Add(provider);
        }

        _providers = deduped;
        _byName = byName;
        ConfigurationErrors = configErrors;
        _aliases = aliases is { Count: > 0 }
            ? new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase)
            : null;
        // Resolve the default through the alias map once so every downstream consumer
        // (walk order, DefaultWritableProvider, the UI's DefaultProviderName) sees the
        // concrete provider the active env actually bound.
        _defaultProviderName = ResolveAlias(defaultProviderName);
        _strict = strict;
    }

    public IReadOnlyList<IVariableProvider> Providers => _providers;

    /// <summary>Non-fatal problems found while composing the registry — currently duplicate
    /// provider names. Surfaced so a caller can warn without the workspace failing to load.</summary>
    public IReadOnlyList<WorkspaceError> ConfigurationErrors { get; }

    /// <summary>The effective default provider name (env &gt; workspace &gt; system, aliases
    /// already resolved), or <c>null</c> if unset. Surfaced so the renderer/UI can tell users
    /// which provider a bare <c>{{name}}</c> token will hit first.</summary>
    public string? DefaultProviderName => _defaultProviderName;

    /// <summary>Alias → provider-name map contributed by the active env, or <c>null</c> when
    /// the env declares none. Exposed so the UI can show "kv → kv-prod" style chips.</summary>
    public IReadOnlyDictionary<string, string>? Aliases => _aliases;

    /// <summary>True when the active env forbids bare-token fall-through past its default
    /// provider. Only meaningful while <see cref="DefaultProviderName"/> is set.</summary>
    public bool StrictVariables => _strict;

    public IReadOnlyList<VariableResolution> Trace => _trace;

    /// <summary>Clear-text values of every secret this registry resolved so far. Consumed by
    /// the renderer to build a <see cref="Rendering.SecretRedactor"/> for the render — the one
    /// legitimate reason to read secret values back out, since redaction needs to know what to
    /// look for. Read it after all expansion is done; the registry is per-render, so the cache
    /// at that point holds exactly this render's lookups.</summary>
    public IReadOnlyList<string> SecretValues
        => _cache.Values
            .Where(v => v is { IsSecret: true, Value.Length: > 0 })
            .Select(v => v!.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Maps an alias to its concrete provider name; names that aren't aliases pass
    /// through unchanged. Null in, null out.</summary>
    private string? ResolveAlias(string? name)
        => name is not null && _aliases is not null && _aliases.TryGetValue(name, out var target)
            ? target
            : name;

    /// <summary>The provider that receives writes when a UI/API action sets a variable without
    /// naming a target provider explicitly. Falls back to the first ReadWrite provider in
    /// registration order if no default is configured. Returns <c>null</c> when no provider
    /// accepts writes.</summary>
    public IVariableProvider? DefaultWritableProvider
    {
        get
        {
            if (_defaultProviderName is not null
                && _byName.TryGetValue(_defaultProviderName, out var named)
                && named.Mode == ProviderMode.ReadWrite)
            {
                return named;
            }
            return _providers.FirstOrDefault(p => p.Mode == ProviderMode.ReadWrite);
        }
    }

    public IVariableProvider? Get(string providerName)
        => _byName.TryGetValue(ResolveAlias(providerName)!, out var p) ? p : null;

    /// <summary>Resolve <c>{{provider:name}}</c>. <paramref name="providerName"/> may be an
    /// env alias — it's mapped to the concrete provider first. Throws if the provider (or
    /// the alias target) doesn't exist or the name isn't present in it.</summary>
    public async ValueTask<VariableValue> ResolveExplicitAsync(string providerName, string name, CancellationToken ct)
    {
        var actualName = ResolveAlias(providerName)!;
        if (!_byName.TryGetValue(actualName, out var provider))
        {
            var viaAlias = !string.Equals(actualName, providerName, StringComparison.OrdinalIgnoreCase);
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_UNKNOWN_PROVIDER,
                viaAlias
                    ? $"Alias '{providerName}' points to provider '{actualName}', which is not registered. " +
                      $"Registered: [{string.Join(", ", _providers.Select(p => p.Name))}]"
                    : $"No variable provider registered with name '{providerName}'. " +
                      $"Registered: [{string.Join(", ", _providers.Select(p => p.Name))}]" +
                      (_aliases is null ? string.Empty : $" Aliases: [{string.Join(", ", _aliases.Keys)}]")));
        }

        // Cache/trace under the concrete provider name so `{{kv:x}}` and `{{kv-prod:x}}`
        // share one lookup when the alias points there.
        var key = provider.Name + ":" + name;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached ?? FailMissing(providerName, name);
        }

        var sw = Stopwatch.StartNew();
        VariableValue? value;
        try
        {
            value = await provider.GetAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_RESOLUTION_FAILED,
                $"Provider '{provider.Name}' threw resolving '{name}': {ex.Message}"));
        }
        sw.Stop();

        _cache[key] = value;
        _trace.Add(new VariableResolution(provider.Name, name, value is not null, value?.IsSecret ?? false, sw.Elapsed));

        return value ?? FailMissing(providerName, name);
    }

    /// <summary>Resolve <c>{{name}}</c>. Tries the effective default provider first when one
    /// is configured, then walks the rest in registration order; the first non-null hit
    /// wins. In strict mode (env binding with <c>strictVariables: true</c>) the walk stops
    /// after the default provider — a miss there is a miss, never another provider's value.
    /// Returns <c>null</c> when nothing supplies the name (the caller — usually
    /// <see cref="Rendering.Interpolation"/> — chooses whether that's fatal).</summary>
    public async ValueTask<VariableValue?> ResolveAnyAsync(string name, CancellationToken ct)
    {
        // Default provider first when configured. Registration order is the fallback so a
        // workspace without a default still gets deterministic behaviour.
        IVariableProvider? defaultProvider = null;
        if (_defaultProviderName is not null && _byName.TryGetValue(_defaultProviderName, out var d))
            defaultProvider = d;

        if (defaultProvider is not null)
        {
            var hit = await TryGetFromAsync(defaultProvider, name, ct).ConfigureAwait(false);
            if (hit is not null) return hit;
            // Strict env binding: never read a same-named value from another provider —
            // with one vault per environment that would silently cross environments.
            if (_strict) return null;
        }

        foreach (var provider in _providers)
        {
            // Already consulted above — don't double-charge the trace.
            if (ReferenceEquals(provider, defaultProvider)) continue;
            var hit = await TryGetFromAsync(provider, name, ct).ConfigureAwait(false);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Single-provider cached lookup that records a trace entry on the first hit.
    /// Shared by <see cref="ResolveAnyAsync"/> so the default-first walk and the registration
    /// walk go through identical bookkeeping.</summary>
    private async ValueTask<VariableValue?> TryGetFromAsync(IVariableProvider provider, string name, CancellationToken ct)
    {
        var key = provider.Name + ":" + name;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var sw = Stopwatch.StartNew();
        VariableValue? value;
        try
        {
            value = await provider.GetAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_RESOLUTION_FAILED,
                $"Provider '{provider.Name}' threw resolving '{name}': {ex.Message}"));
        }
        sw.Stop();
        _cache[key] = value;
        _trace.Add(new VariableResolution(provider.Name, name, value is not null, value?.IsSecret ?? false, sw.Elapsed));
        return value;
    }

    private static VariableValue FailMissing(string providerName, string name) =>
        throw new WorkspaceParseException(new WorkspaceError(
            WorkspaceErrorCode.E_PROVIDER_RESOLUTION_FAILED,
            $"Variable '{providerName}:{name}' resolved to null."));
}

/// <summary>Records one provider hit during a render. Stored in execution history by
/// reference text only — values are never persisted in the trace.</summary>
public sealed record VariableResolution(
    string ProviderName,
    string Name,
    bool Resolved,
    bool IsSecret,
    TimeSpan Duration);
