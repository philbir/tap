using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Auth;

/// <summary>
/// The scope an auth profile resolves against: the owning collection plus the environment the
/// caller has selected. A profile under <c>collections/&lt;slug&gt;/</c> is owned by that
/// collection and sees its variables when its fields are expanded; a profile under
/// <c>auth/</c> is workspace-scoped and carries a null <see cref="Collection"/>. Every profile,
/// wherever it lives, sees the applicable <see cref="Env"/>.
/// </summary>
/// <param name="Collection">Owning collection, or <c>null</c> for a workspace-scoped profile.</param>
/// <param name="Env">Environment in effect, or <c>null</c> when the caller selected none, the
/// workspace declares no <c>defaultEnv</c>, or the selected env is scoped away from this
/// profile's collection.</param>
public readonly record struct AuthContext(CollectionFile? Collection, EnvFile? Env)
{
    /// <summary>Cache identity for a token minted under this context.</summary>
    public AuthProfileScope ScopeFor(string authPath) => new(authPath, Env?.RelativePath);
}

/// <summary>
/// Identity of a cached runtime token: the auth profile plus the environment that was in effect
/// when it was minted. Two environments can point a profile at different token endpoints — a
/// client id or authority that comes out of <c>dev.env.tap</c> mints a different token than the
/// same profile resolved against <c>prod.env.tap</c> — so they may never share a cache entry. A
/// run with no env carries a null <see cref="Env"/>.
/// </summary>
public readonly record struct AuthProfileScope(string Path, string? Env);

/// <summary>
/// Locates the <see cref="AuthContext"/> for an auth profile. Mirrors the collection/env
/// resolution <see cref="WorkspaceRenderer"/> performs for requests, so a profile expands its
/// fields against exactly the variables a request in the same collection would see.
/// </summary>
public static class AuthScopeResolver
{
    /// <summary>
    /// Resolve the context for the profile at <paramref name="authPath"/>. Pass <c>null</c> for
    /// the path to get an env-only context (the discovery endpoint expands an authority the user
    /// is still typing, before any profile is bound).
    ///
    /// <para><paramref name="envPath"/> is the env the caller has selected. A <b>global</b> env
    /// always applies. A <b>scoped</b> env applies only to the collections it names — and the
    /// collection that counts is the <em>profile's</em>, not the caller's: a request in
    /// collection A that borrows a profile from collection B must not drag A's environment
    /// across, or B would resolve against variables that mean something different there.</para>
    /// </summary>
    public static AuthContext ContextFor(
        LoadedWorkspace workspace, string? authPath, string? envPath = null)
    {
        var env = ResolveEnv(workspace, envPath);
        var collection = authPath is null ? null : CollectionLocator.ForFile(workspace, authPath);
        var slug = collection is null ? null : CollectionLocator.SlugForFile(collection.RelativePath);

        return new AuthContext(collection, env?.AppliesTo(slug) == true ? env : null);
    }

    /// <summary>
    /// The caller's env when it named one, the workspace's <c>defaultEnv</c> when it didn't.
    ///
    /// <para>A named env that isn't in the workspace resolves to <c>null</c> rather than falling
    /// back to the default. Silently swapping in a different env is the failure this whole
    /// parameter exists to prevent: the profile would resolve against variables the user never
    /// selected and either mint a token against the wrong endpoint or fail with a misleading
    /// "unknown variable". No env layer at all produces the same error honestly.</para>
    /// </summary>
    private static EnvFile? ResolveEnv(LoadedWorkspace workspace, string? envPath)
    {
        if (envPath is { Length: > 0 })
            return workspace.FindByPath(envPath) as EnvFile;

        return workspace.Manifest?.DefaultEnv is { } defaultRef
            ? workspace.Resolve(defaultRef) as EnvFile
            : null;
    }
}
