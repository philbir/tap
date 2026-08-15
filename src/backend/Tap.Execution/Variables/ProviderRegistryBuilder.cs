using System.Text;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Execution.Variables;

/// <summary>
/// Composes a <see cref="VariableProviderRegistry"/> for one workspace by collecting:
/// <list type="bullet">
///   <item>System-level provider configs from <see cref="StudioOptions.SystemProviders"/>
///     (loaded from <c>Studio:Providers:[]</c> in app config).</item>
///   <item>Workspace-level provider configs from <c>WorkspaceManifestFile.Providers</c>.</item>
/// </list>
///
/// <para>Workspace providers shadow same-named system providers — useful for letting a
/// workspace override a corporate-default azkv with its own vault without losing the
/// global registration. The factory for each <see cref="VariableProviderConfig.Type"/>
/// is looked up from the injected <see cref="IVariableProviderFactory"/> set; unknown
/// types are skipped with a warning so a typo in config doesn't crash the studio.</para>
///
/// <para><b>Instance cache.</b> Provider instances are reused across <see cref="Build"/>
/// calls for the same workspace root + config hash. This is what keeps an
/// <c>AzureKeyVaultVariableProvider</c>'s <c>DefaultAzureCredential</c> chain warm and its
/// list cache hot across renders — without it, every <c>/api/variables/views</c> call paid
/// the full credential-probe + KV-listing latency. The cache is invalidated entry-by-entry
/// when a config's hash changes, and wiped wholesale when the workspace root changes
/// (so a <c>FileVariableProvider</c> pinned to the old path doesn't leak across workspace
/// switches). The fresh <see cref="VariableProviderRegistry"/> returned each call still
/// owns its own per-render resolution cache + trace — only the underlying providers are
/// shared.</para>
/// </summary>
public sealed class ProviderRegistryBuilder(IEnumerable<IVariableProviderFactory> factories, ILogger<ProviderRegistryBuilder> logger)
{
    private readonly Dictionary<string, IVariableProviderFactory> _factories =
        factories.ToDictionary(f => f.Type, StringComparer.OrdinalIgnoreCase);

    private readonly Lock _cacheGate = new();
    private readonly Dictionary<string, CachedProvider> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _cachedWorkspaceRoot;

    private sealed record CachedProvider(IVariableProvider Provider, string ConfigHash);

    public VariableProviderRegistry Build(
        LoadedWorkspace workspace,
        string workspaceRoot,
        IReadOnlyList<VariableProviderConfig> systemProviders,
        string? systemDefaultProvider = null,
        EnvFile? env = null)
    {
        var workspaceConfigs = workspace.Manifest?.VariableProviders ?? [];
        var workspaceNames = new HashSet<string>(workspaceConfigs.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        // Build the effective config list (workspace shadowing system) once, in the order
        // we want the registry to walk them.
        var effective = new List<VariableProviderConfig>(workspaceConfigs.Count + systemProviders.Count);
        foreach (var cfg in workspaceConfigs) effective.Add(cfg);
        foreach (var cfg in systemProviders)
        {
            if (workspaceNames.Contains(cfg.Name)) continue;
            effective.Add(cfg);
        }

        var providers = new List<IVariableProvider>(effective.Count);
        var context = new ProviderFactoryContext(workspaceRoot);

        lock (_cacheGate)
        {
            // Workspace switch wipes the cache: providers like FileVariableProvider close
            // over the old workspace root and would otherwise read/write the wrong .tap dir.
            if (!string.Equals(_cachedWorkspaceRoot, workspaceRoot, StringComparison.Ordinal))
            {
                _cache.Clear();
                _cachedWorkspaceRoot = workspaceRoot;
            }

            var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in effective)
            {
                liveNames.Add(cfg.Name);
                var hash = HashConfig(cfg);

                if (_cache.TryGetValue(cfg.Name, out var cached) && cached.ConfigHash == hash)
                {
                    providers.Add(cached.Provider);
                    continue;
                }

                if (TryInstantiate(cfg, context) is not { } fresh) continue;
                _cache[cfg.Name] = new CachedProvider(fresh, hash);
                providers.Add(fresh);
            }

            // Evict cache entries for providers that no longer exist in the effective config.
            if (_cache.Count > liveNames.Count)
            {
                foreach (var stale in _cache.Keys.Where(k => !liveNames.Contains(k)).ToList())
                {
                    _cache.Remove(stale);
                }
            }
        }

        // Default precedence mirrors the variable cascade: env > workspace > system. The
        // env's aliases + strict flag ride along so switching envs re-points bare tokens
        // and alias prefixes without touching any provider declaration.
        var defaultProvider = env?.DefaultVariableProvider
            ?? workspace.Manifest?.DefaultVariableProvider
            ?? systemDefaultProvider;

        if (env?.ProviderAliases is { Count: > 0 } aliasMap)
        {
            foreach (var (alias, target) in aliasMap)
            {
                if (!providers.Any(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogWarning(
                        "Env '{Env}' aliases '{Alias}' to provider '{Target}', which is not registered.",
                        env.RelativePath, alias, target);
                }
            }
        }

        return new VariableProviderRegistry(
            providers,
            defaultProvider,
            env?.ProviderAliases,
            env?.StrictVariables ?? false);
    }

    private IVariableProvider? TryInstantiate(VariableProviderConfig cfg, ProviderFactoryContext context)
    {
        if (!_factories.TryGetValue(cfg.Type, out var factory))
        {
            logger.LogWarning("Unknown variable provider type '{Type}' for provider '{Name}' — skipping.", cfg.Type, cfg.Name);
            return null;
        }
        try
        {
            return factory.Create(StripSystemScopeOnly(cfg, factory.Descriptor), context);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to instantiate variable provider '{Name}' (type {Type}).", cfg.Name, cfg.Type);
            return null;
        }
    }

    /// <summary>
    /// Drops settings the descriptor marks system-scope-only when the config came out of the
    /// workspace. <c>tap.md</c> is cloned along with a repository, so a field like 1Password's
    /// <c>cliPath</c> — which decides what binary gets spawned, on a code path as innocuous as
    /// rendering the variables panel — must never be honoured from there. The drop is logged so
    /// the setting doesn't just silently do nothing.
    /// </summary>
    private VariableProviderConfig StripSystemScopeOnly(VariableProviderConfig cfg, ProviderTypeDescriptor descriptor)
    {
        if (cfg.Origin != ProviderOrigin.Workspace) return cfg;

        var blocked = new HashSet<string>(descriptor.SystemScopeOnlyFieldKeys, StringComparer.OrdinalIgnoreCase);
        if (blocked.Count == 0) return cfg;

        var dropped = cfg.Settings
            .Where(kv => blocked.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .ToArray();
        if (dropped.Length == 0) return cfg;

        logger.LogWarning(
            "Provider '{Name}' (type {Type}) sets {Keys} in workspace frontmatter — ignored. "
            + "These settings are only read from system-scope provider configuration.",
            cfg.Name, cfg.Type, string.Join(", ", dropped));

        var settings = cfg.Settings
            .Where(kv => !blocked.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return cfg with { Settings = settings };
    }

    private static string HashConfig(VariableProviderConfig cfg)
    {
        var sb = new StringBuilder();
        sb.Append(cfg.Type).Append('|').Append(cfg.Origin).Append('|');
        foreach (var kv in cfg.Settings.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? string.Empty).Append('|');
        }
        return sb.ToString();
    }
}
