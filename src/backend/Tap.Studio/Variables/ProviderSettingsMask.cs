using Tap.Workspace.Variables;

namespace Tap.Studio.Variables;

/// <summary>
/// Descriptor-driven handling of sensitive provider settings. A setting is sensitive when
/// its type descriptor declares the field as <see cref="ProviderFieldKind.Secret"/>; for
/// configs whose type has no registered factory (typo, plugin not loaded) we fall back to a
/// conservative name heuristic so an unknown type never leaks a stored secret.
///
/// <para>Round-trip rule shared by every settings-editing endpoint: values leave the server
/// as <see cref="Mask"/>, and a value that comes back as the literal mask means "keep what's
/// stored" — the client never sees or re-sends the real value.</para>
/// </summary>
public static class ProviderSettingsMask
{
    public const string Mask = "***";

    public static ProviderTypeDescriptor? DescriptorFor(IEnumerable<IVariableProviderFactory> factories, string type)
        => factories.FirstOrDefault(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase))?.Descriptor;

    public static bool IsSecretKey(ProviderTypeDescriptor? descriptor, string key)
    {
        if (descriptor is not null)
        {
            return descriptor.Fields.Any(f =>
                f.Kind == ProviderFieldKind.Secret
                && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        }
        return key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, string?> Apply(
        ProviderTypeDescriptor? descriptor,
        IReadOnlyDictionary<string, string?> settings)
    {
        var masked = new Dictionary<string, string?>(settings.Count);
        foreach (var (k, v) in settings)
        {
            masked[k] = IsSecretKey(descriptor, k) && !string.IsNullOrEmpty(v) ? Mask : v;
        }
        return masked;
    }

    /// <summary>Reverses <see cref="Apply"/> on inbound settings: a secret key whose value is
    /// the literal mask takes the stored value instead (or is dropped when nothing is stored).
    /// Everything else is taken at face value.</summary>
    public static Dictionary<string, string?> RestoreMasked(
        ProviderTypeDescriptor? descriptor,
        IReadOnlyDictionary<string, string?> incoming,
        IReadOnlyDictionary<string, string?>? stored)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in incoming)
        {
            if (v == Mask && IsSecretKey(descriptor, k))
            {
                if (stored is not null && stored.TryGetValue(k, out var prev))
                    result[k] = prev;
                continue;
            }
            result[k] = v;
        }
        return result;
    }

    /// <summary>Label of the first required descriptor field that's missing or blank in
    /// <paramref name="settings"/>, or null when the config satisfies the descriptor.</summary>
    public static string? FirstMissingRequired(
        ProviderTypeDescriptor descriptor,
        IReadOnlyDictionary<string, string?> settings)
        => descriptor.Fields.FirstOrDefault(f =>
                f.Required && (!settings.TryGetValue(f.Key, out var v) || string.IsNullOrWhiteSpace(v)))
            ?.Label;
}
