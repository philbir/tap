using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Auth;

/// <summary>
/// The scope an auth profile resolves against: the owning collection (and its effective
/// stage) plus the environment the caller has selected. A profile under
/// <c>collections/&lt;slug&gt;/</c> is owned by that collection and sees its variables when
/// its fields are expanded; a profile under <c>auth/</c> is workspace-scoped and carries a
/// null <see cref="Collection"/>. Every profile, wherever it lives, sees the active
/// <see cref="Env"/>.
/// </summary>
/// <param name="Collection">Owning collection, or <c>null</c> for a workspace-scoped profile.</param>
/// <param name="Stage">Effective stage of <paramref name="Collection"/>, or <c>null</c> when
/// the collection defines none.</param>
/// <param name="Env">Environment in effect, or <c>null</c> when the caller selected none and
/// the workspace declares no <c>defaultEnv</c>.</param>
public readonly record struct AuthContext(CollectionFile? Collection, CollectionStage? Stage, EnvFile? Env)
{
    /// <summary>Cache identity for a token minted under this context.</summary>
    public AuthProfileScope ScopeFor(string authPath) => new(authPath, Stage?.Name, Env?.RelativePath);
}

/// <summary>
/// Identity of a cached runtime token: the auth profile plus the collection stage and the
/// environment that were in effect when it was minted. Two stages of the same collection can
/// point a profile at different token endpoints, and so can two environments — a client id or
/// authority that comes out of <c>dev.env.md</c> mints a different token than the same profile
/// resolved against <c>prod.env.md</c>. None of those may share a cache entry. Workspace-scoped
/// profiles carry a null <see cref="Stage"/>; a workspace with no env carries a null
/// <see cref="Env"/>.
/// </summary>
public readonly record struct AuthProfileScope(string Path, string? Stage, string? Env);

/// <summary>
/// Locates the <see cref="AuthContext"/> for an auth profile. Mirrors the collection/stage/env
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
    /// <para><paramref name="stageName"/> is the stage the caller has selected (the request
    /// editor's stage picker). It is only honored when it belongs to the profile's own
    /// collection: a request in collection A that borrows an auth profile from collection B
    /// must not smuggle A's stage name across, or B would silently resolve against a stage
    /// that means something different. When it doesn't apply, the profile's collection falls
    /// back to its own <see cref="CollectionFile.DefaultStage"/>.</para>
    ///
    /// <para><paramref name="envPath"/> is the env the caller has selected. Unlike the stage it
    /// always applies — an env is a property of the session, not of a collection.</para>
    /// </summary>
    public static AuthContext ContextFor(
        LoadedWorkspace workspace, string? authPath, string? requestPath = null,
        string? stageName = null, string? envPath = null)
    {
        var env = ResolveEnv(workspace, envPath);

        var collection = authPath is null ? null : CollectionLocator.ForFile(workspace, authPath);
        if (collection is null) return new AuthContext(null, null, env);

        var applies = requestPath is null
            || (CollectionLocator.ForFile(workspace, requestPath) is { } requestCollection
                && string.Equals(requestCollection.RelativePath, collection.RelativePath, StringComparison.OrdinalIgnoreCase));

        var stage = (applies ? collection.FindStage(stageName) : null)
                    ?? collection.FindStage(collection.DefaultStage);
        return new AuthContext(collection, stage, env);
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
