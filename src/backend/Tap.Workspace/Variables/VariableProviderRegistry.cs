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
    private readonly string? _defaultProviderName;
    private readonly List<VariableResolution> _trace = [];
    private readonly Dictionary<string, VariableValue?> _cache = new(StringComparer.Ordinal);

    public VariableProviderRegistry(IEnumerable<IVariableProvider> providers, string? defaultProviderName)
    {
        _providers = [.. providers];
        _byName = _providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _defaultProviderName = defaultProviderName;
    }

    public IReadOnlyList<IVariableProvider> Providers => _providers;

    /// <summary>The configured default provider name, or <c>null</c> if unset. Surfaced so
    /// the renderer/UI can tell users which provider a bare <c>{{name}}</c> token will hit
    /// first.</summary>
    public string? DefaultProviderName => _defaultProviderName;

    public IReadOnlyList<VariableResolution> Trace => _trace;

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
        => _byName.TryGetValue(providerName, out var p) ? p : null;

    /// <summary>Resolve <c>{{provider:name}}</c>. Throws if the provider doesn't exist or
    /// the name isn't present in it.</summary>
    public async ValueTask<VariableValue> ResolveExplicitAsync(string providerName, string name, CancellationToken ct)
    {
        if (!_byName.TryGetValue(providerName, out var provider))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_UNKNOWN_PROVIDER,
                $"No variable provider registered with name '{providerName}'. " +
                $"Registered: [{string.Join(", ", _providers.Select(p => p.Name))}]"));
        }

        var key = providerName + ":" + name;
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
                $"Provider '{providerName}' threw resolving '{name}': {ex.Message}"));
        }
        sw.Stop();

        _cache[key] = value;
        _trace.Add(new VariableResolution(providerName, name, value is not null, value?.IsSecret ?? false, sw.Elapsed));

        return value ?? FailMissing(providerName, name);
    }

    /// <summary>Resolve <c>{{name}}</c>. Tries the workspace's default provider first when
    /// one is configured, then walks the rest in registration order; the first non-null hit
    /// wins. Returns <c>null</c> when no provider supplies the name (the caller — usually
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
