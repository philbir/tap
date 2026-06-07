using Tap.Workspace.Model;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Resolves which collection a request belongs to.
///
/// <para>By convention, every request lives at
/// <c>collections/&lt;slug&gt;/...nested-folders.../*.req.md</c>. The collection's own file
/// is <c>collections/&lt;slug&gt;/_collection.md</c>. This helper walks the request's path
/// to identify the owning collection, returning <c>null</c> for requests that don't sit
/// under <c>collections/</c> (e.g. malformed workspaces or legacy paths).</para>
/// </summary>
public static class CollectionLocator
{
    private const string CollectionsRoot = "collections";
    private const string CollectionFileName = "_collection.md";

    /// <summary>Returns the <see cref="CollectionFile"/> that owns the request at
    /// <paramref name="requestRelativePath"/>, or <c>null</c> if the request doesn't live
    /// under <c>collections/</c> or its owning collection has no <c>_collection.md</c>.</summary>
    public static CollectionFile? ForRequest(LoadedWorkspace workspace, string requestRelativePath)
    {
        var slug = SlugForRequest(requestRelativePath);
        if (slug is null) return null;
        var collectionPath = $"{CollectionsRoot}/{slug}/{CollectionFileName}";
        return workspace.FindByPath(collectionPath) as CollectionFile;
    }

    /// <summary>Returns the collection's slug (the path segment immediately under
    /// <c>collections/</c>) for the given request path, or <c>null</c> if the request
    /// isn't under <c>collections/</c>.</summary>
    public static string? SlugForRequest(string requestRelativePath)
    {
        var parts = requestRelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null; // needs at least collections/<slug>/<file>
        if (!string.Equals(parts[0], CollectionsRoot, StringComparison.OrdinalIgnoreCase)) return null;
        return parts[1];
    }
}
