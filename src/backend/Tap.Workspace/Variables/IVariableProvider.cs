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

    /// <summary>Removes a variable. <c>true</c> when something was removed, <c>false</c> when
    /// the name wasn't there — deleting an absent name is not an error, so a UI can converge on
    /// "gone" without racing itself. Throws <see cref="NotSupportedException"/> when
    /// <see cref="Mode"/> is <see cref="ProviderMode.Read"/>, matching
    /// <see cref="SetAsync"/>.</summary>
    ValueTask<bool> DeleteAsync(string name, CancellationToken ct);
}

/// <summary>Optional capability for providers whose <see cref="IVariableProvider.ListAsync"/>
/// result is cached for the instance's lifetime (azkv). The Studio's browse endpoint calls
/// <see cref="InvalidateListCache"/> on an explicit refresh so a long-lived cached instance
/// can pick up secrets added since the first listing.</summary>
public interface IRefreshableVariableProvider
{
    void InvalidateListCache();
}

/// <summary>
/// Implemented by providers that can say something more useful than "not found" when a lookup
/// misses. A miss on most providers means "you didn't put it there"; on some it means "you
/// didn't set this specific environment variable", and saying which one is the difference
/// between a dead end and a fix.
/// </summary>
public interface IExplainsMissingValues
{
    /// <summary>One sentence appended to the resolution failure for <paramref name="name"/>.</summary>
    string ExplainMiss(string name);
}
