namespace Tap.Workspace.Variables;

/// <summary>
/// Declarative configuration for one provider instance. Built from either system-level
/// app configuration (<c>Studio:VariableProviders:[]</c> in appsettings) or workspace-level
/// frontmatter (<c>variableProviders:</c> in <c>workspace.tap</c>).
///
/// <para><see cref="Name"/> is the user-facing identifier referenced in <c>{{name:var}}</c>
/// tokens and on the variable view's per-provider chips. It must be unique within the
/// registry (system + workspace combined). <see cref="Type"/> selects which
/// <see cref="IVariableProviderFactory"/> to instantiate from.</para>
///
/// <para>Mode is intentionally not on the config — it's a static property of the provider
/// implementation. <c>env</c> is read; <c>file</c> and <c>azkv</c> are read/write. Users
/// pick a <c>type</c> and the mode follows.</para>
///
/// <para><see cref="Settings"/> is an opaque key/value bag interpreted by the factory.
/// Different provider types expect different keys: env reads nothing; file reads
/// <c>encryptionKey</c>; azkv reads <c>vaultName</c>, <c>tenantId</c>, <c>prefix</c>.</para>
/// </summary>
public sealed record VariableProviderConfig
{
    public required string Name { get; init; }
    public required string Type { get; init; }

    /// <summary>Provider-specific options. Values may be null when unset.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>Where this provider was configured. <see cref="ProviderOrigin.System"/> means
    /// it came from app config; <see cref="ProviderOrigin.Workspace"/> means it came from the
    /// active workspace's <c>workspace.tap</c>. Workspace providers can shadow same-named system
    /// providers within the scope of that workspace.</summary>
    public required ProviderOrigin Origin { get; init; }
}

public enum ProviderOrigin
{
    System,
    Workspace,
}
