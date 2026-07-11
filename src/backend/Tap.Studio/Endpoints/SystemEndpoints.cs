using Tap.Studio.Contracts;
using Tap.Studio.Variables;
using Tap.Workspace.Variables;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/system/*</c> — reads and writes the user-level Tap configuration that lives
/// under <c>TAP_SYSTEM_DIR</c> (default <c>~/.tap</c>): system-scope variable providers
/// and system variables.
///
/// <para>Sensitive provider setting values (keys listed in <see cref="SensitiveSettingKeys"/>)
/// are masked in <see cref="MaskSettings"/> on the way out. On save, when the client sends
/// back the literal mask string for a setting, the server preserves the existing on-disk
/// value for that key instead of overwriting it — so a UI round-trip never leaks a stored
/// secret through the API and never clobbers a secret the user didn't intend to edit.</para>
/// </summary>
public static class SystemEndpoints
{
    private const string Mask = "***";

    private static readonly HashSet<string> SensitiveSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "encryptionKey",
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/settings", (
            SystemSettingsStore store,
            IEnumerable<IVariableProviderFactory> factories) =>
        {
            var providers = store.GetProviderConfigs()
                // Hide the implicit `system` entry: the UI never lets users add/edit/remove it.
                .Where(p => !string.Equals(p.Type, SystemVariableProvider.ProviderType, StringComparison.OrdinalIgnoreCase))
                .Select(p => new SystemProviderDto(p.Name, p.Type, MaskSettings(p.Settings)))
                .ToArray();
            var variables = store.GetVariables()
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new SystemVariableDto(kv.Key, kv.Value.Secret ? string.Empty : kv.Value.Value, kv.Value.Secret))
                .ToArray();
            var availableTypes = factories
                .Select(f => f.Type)
                .Where(t => !string.Equals(t, SystemVariableProvider.ProviderType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray();

            return Results.Ok(new SystemSettingsDto(
                store.SystemDir,
                store.DefaultVariableProvider,
                providers,
                variables,
                availableTypes));
        });

        app.MapPut("/api/system/settings", (SaveSystemSettingsDto body, SystemSettingsStore store) =>
        {
            var previousProviders = store.GetProviderConfigs()
                .Where(p => !string.Equals(p.Type, SystemVariableProvider.ProviderType, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var previousVars = store.GetVariables();

            var providers = new List<StoredProvider>(body.VariableProviders?.Count ?? 0);
            foreach (var p in body.VariableProviders ?? Array.Empty<SystemProviderDto>())
            {
                if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Type)) continue;
                var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
                previousProviders.TryGetValue(p.Name, out var prev);
                foreach (var (k, v) in p.Settings ?? new Dictionary<string, string?>())
                {
                    if (SensitiveSettingKeys.Contains(k) && v == Mask)
                    {
                        // Mask round-tripped unchanged — keep whatever's on disk for this key.
                        if (prev?.Settings.TryGetValue(k, out var prevValue) == true)
                            settings[k] = prevValue;
                        continue;
                    }
                    settings[k] = v;
                }
                providers.Add(new StoredProvider { Name = p.Name, Type = p.Type, Settings = settings });
            }

            try
            {
                store.ReplaceProviders(body.DefaultVariableProvider, providers);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = "duplicate-provider", message = ex.Message });
            }

            // Variables: empty value on a secret entry means "keep what's there" — same
            // round-trip rule as provider settings. Anything else is taken at face value.
            var variables = new Dictionary<string, StoredVariable>(StringComparer.Ordinal);
            foreach (var v in body.Variables ?? Array.Empty<SystemVariableDto>())
            {
                if (string.IsNullOrWhiteSpace(v.Name)) continue;
                if (v.Secret && string.IsNullOrEmpty(v.Value)
                    && previousVars.TryGetValue(v.Name, out var prev))
                {
                    variables[v.Name] = prev with { Secret = true };
                    continue;
                }
                variables[v.Name] = new StoredVariable(v.Value ?? string.Empty, v.Secret);
            }
            store.ReplaceVariables(variables);

            return Results.NoContent();
        });
    }

    private static IReadOnlyDictionary<string, string?> MaskSettings(IReadOnlyDictionary<string, string?> settings)
    {
        var masked = new Dictionary<string, string?>(settings.Count);
        foreach (var (k, v) in settings)
        {
            masked[k] = SensitiveSettingKeys.Contains(k) && !string.IsNullOrEmpty(v) ? Mask : v;
        }
        return masked;
    }
}
