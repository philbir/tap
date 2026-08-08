namespace Tap.Workspace.Variables;

/// <summary>
/// Creates <see cref="IVariableProvider"/> instances for a specific provider type. Registered
/// in DI; the registry looks up factories by <see cref="Type"/> when materializing
/// <see cref="VariableProviderConfig"/> entries from app + workspace configuration.
///
/// <para>Add a new provider type by implementing this interface, registering the
/// implementation as <c>IVariableProviderFactory</c>, and consumers can immediately reference
/// the type from system or workspace configuration without any other code change.</para>
/// </summary>
public interface IVariableProviderFactory
{
    /// <summary>The <c>type:</c> identifier used in configuration (e.g. <c>env</c>, <c>file</c>, <c>azkv</c>).</summary>
    string Type { get; }

    /// <summary>User-facing metadata for this type: display name, icon key, description, and
    /// the typed settings schema. The Studio serves this to the UI so the provider picker and
    /// settings form are generated rather than hard-coded per type.</summary>
    ProviderTypeDescriptor Descriptor { get; }

    /// <summary>Instantiates a provider from one configuration entry. <paramref name="context"/>
    /// gives the factory access to ambient services it needs (workspace root for the file
    /// provider, etc.). Throws when the config is malformed for this type.</summary>
    IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context);
}

/// <summary>Per-instantiation context passed to a factory. Holds the absolute workspace root
/// (for the file provider) and an opportunity for future per-workspace services.</summary>
public sealed record ProviderFactoryContext(string WorkspaceRoot);
