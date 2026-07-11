namespace Tap.Workspace.Variables;

/// <summary>
/// One configured source of variables. Each provider owns a flat namespace of names. The
/// registry composes multiple providers into a single resolution view.
///
/// <para>Providers MUST NOT log, echo, or otherwise expose resolved values. Returning
/// <c>null</c> from <see cref="GetAsync"/> means "not in this provider" (distinct from
/// throwing, which signals the provider itself is broken).</para>
/// </summary>
public interface IVariableProvider
{
    /// <summary>Stable, unique name used in <c>{{name:var}}</c> tokens and in the UI.</summary>
    string Name { get; }

    /// <summary>Whether this provider accepts writes via <see cref="SetAsync"/>.</summary>
    ProviderMode Mode { get; }

    /// <summary>The configuration that produced this instance — useful for the UI to surface
    /// where a provider came from (system vs. workspace) and what its settings are.</summary>
    VariableProviderConfig Config { get; }

    /// <summary>Resolves a single name. <c>null</c> = not present here.</summary>
    ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct);

    /// <summary>Enumerates every variable this provider can produce right now. Used by the
    /// variables panel and by the cascade resolver when assembling the merged-by-name view.
    /// May be expensive for remote providers (azkv) — implementations should cache where
    /// reasonable.</summary>
    ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct);

    /// <summary>Writes a value. Throws <see cref="NotSupportedException"/> when
    /// <see cref="Mode"/> is <see cref="ProviderMode.Read"/>. <paramref name="isSecret"/>
    /// instructs the provider whether to apply at-rest encryption (file provider) or
    /// otherwise treat the value as sensitive.</summary>
    ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct);
}
