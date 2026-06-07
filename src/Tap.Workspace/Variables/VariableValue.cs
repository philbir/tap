namespace Tap.Workspace.Variables;

/// <summary>
/// A variable value as produced by a provider. Carries enough metadata for the resolver to
/// decide whether to mask the value in UI output and which provider it originated from.
///
/// <para><see cref="Value"/> is always the clear-text value — encryption-at-rest concerns
/// (file provider's shared-secret crypto) are unwrapped by the provider before this record
/// crosses the provider boundary. Callers must respect <see cref="IsSecret"/> and never
/// surface <see cref="Value"/> in any UI-facing payload when it's true.</para>
/// </summary>
/// <param name="Name">Variable name as referenced in <c>{{name}}</c> tokens.</param>
/// <param name="Value">Clear-text value, or empty if the provider only knows the name (e.g.
/// Key Vault listings before per-name fetch).</param>
/// <param name="IsSecret">Whether the value is sensitive — UI must mask, history must not log.</param>
/// <param name="ProviderName">Originating provider's <see cref="IVariableProvider.Name"/>.</param>
public sealed record VariableValue(
    string Name,
    string Value,
    bool IsSecret,
    string ProviderName);
