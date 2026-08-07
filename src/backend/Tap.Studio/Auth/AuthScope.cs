using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Auth;

/// <summary>
/// The collection context an auth profile resolves against. A profile under
/// <c>collections/&lt;slug&gt;/</c> is owned by that collection and sees its variables (and
/// the active stage's) when its fields are expanded; a profile under <c>auth/</c> is
/// workspace-scoped and carries a null <see cref="Collection"/>.
/// </summary>
/// <param name="Collection">Owning collection, or <c>null</c> for a workspace-scoped profile.</param>
/// <param name="Stage">Effective stage of <paramref name="Collection"/>, or <c>null</c> when
/// the collection defines none.</param>
public readonly record struct AuthContext(CollectionFile? Collection, CollectionStage? Stage)
{
    /// <summary>Cache identity for a token minted under this context.</summary>
    public AuthProfileScope ScopeFor(string authPath) => new(authPath, Stage?.Name);
}

/// <summary>
/// Identity of a cached runtime token: the auth profile plus the collection stage that was
/// in effect when it was minted. Two stages of the same collection can point a profile at
/// different token endpoints, so they must not share a cache entry. Workspace-scoped
/// profiles (and collections without stages) carry a null <see cref="Stage"/> and key
/// exactly as they did before collection-scoped auth existed.
/// </summary>
public readonly record struct AuthProfileScope(string Path, string? Stage);

/// <summary>
/// Locates the <see cref="AuthContext"/> for an auth profile. Mirrors the collection/stage
/// resolution <see cref="WorkspaceRenderer"/> performs for requests, so a profile expands its
/// fields against exactly the variables a request in the same collection would see.
/// </summary>
public static class AuthScopeResolver
{
    /// <summary>
    /// Resolve the context for the profile at <paramref name="authPath"/>.
    ///
    /// <para><paramref name="stageName"/> is the stage the caller has selected (the request
    /// editor's stage picker). It is only honored when it belongs to the profile's own
    /// collection: a request in collection A that borrows an auth profile from collection B
    /// must not smuggle A's stage name across, or B would silently resolve against a stage
    /// that means something different. When it doesn't apply, the profile's collection falls
    /// back to its own <see cref="CollectionFile.DefaultStage"/>.</para>
    /// </summary>
    public static AuthContext ContextFor(
        LoadedWorkspace workspace, string authPath, string? requestPath = null, string? stageName = null)
    {
        var collection = CollectionLocator.ForFile(workspace, authPath);
        if (collection is null) return default;

        var applies = requestPath is null
            || (CollectionLocator.ForFile(workspace, requestPath) is { } requestCollection
                && string.Equals(requestCollection.RelativePath, collection.RelativePath, StringComparison.OrdinalIgnoreCase));

        var stage = (applies ? collection.FindStage(stageName) : null)
                    ?? collection.FindStage(collection.DefaultStage);
        return new AuthContext(collection, stage);
    }
}
