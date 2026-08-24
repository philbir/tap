using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Resolves which collection a workspace file belongs to.
///
/// <para>By convention, everything owned by a collection lives at
/// <c>collections/&lt;slug&gt;/...nested-folders.../*</c> — requests (<c>*.req.tap</c>) and,
/// optionally, auth profiles (<c>*.auth.tap</c>) that want the collection's variables in
/// scope. The collection's own file is <c>collections/&lt;slug&gt;/_collection.tap</c>.
/// This helper walks a file's path to identify the owning collection, returning <c>null</c>
/// for files that don't sit under <c>collections/</c> (workspace-scoped auth profiles under
/// <c>auth/</c>, environments, or malformed/legacy paths).</para>
/// </summary>
public static class CollectionLocator
{
    public const string CollectionsRoot = "collections";

    /// <summary>Returns the <see cref="CollectionFile"/> that owns the file at
    /// <paramref name="relativePath"/>, or <c>null</c> if the file doesn't live under
    /// <c>collections/</c> or its owning collection has no collection file.</summary>
    public static CollectionFile? ForFile(LoadedWorkspace workspace, string relativePath)
    {
        var slug = SlugForFile(relativePath);
        if (slug is null) return null;
        return ForSlug(workspace, slug);
    }

    /// <summary>
    /// The collection that owns a request: its explicit <c># @tap-collection</c> reference when
    /// it declared one, otherwise its location under <c>collections/</c>.
    ///
    /// <para>Every attribution decision — rendering, agent-access policy, inventory — must go
    /// through here rather than through <see cref="ForFile"/>, or a <c>.http</c> file that
    /// claimed a collection would inherit its baseUrl while being denied by its agent policy.</para>
    /// </summary>
    public static CollectionFile? ForRequest(LoadedWorkspace workspace, RequestFile request)
        => request.CollectionRef is { } slug
            ? ForSlug(workspace, slug)
            : ForFile(workspace, request.RelativePath);

    /// <summary>Path-based overload of <see cref="ForRequest"/>, for callers that hold only a
    /// path. Looks the request up so an explicit <c># @tap-collection</c> is honored there too;
    /// falls back to location for paths that aren't requests.</summary>
    public static CollectionFile? ForRequestPath(LoadedWorkspace workspace, string relativePath)
        => workspace.FindByPath(relativePath) is RequestFile request
            ? ForRequest(workspace, request)
            : ForFile(workspace, relativePath);

    /// <summary>Returns the <see cref="CollectionFile"/> for a slug, accepting either extension
    /// family so a workspace mid-migration still attributes its requests correctly.</summary>
    public static CollectionFile? ForSlug(LoadedWorkspace workspace, string slug)
        => workspace.FindByPath($"{CollectionsRoot}/{slug}/{KindResolver.CollectionFileName}") as CollectionFile
        ?? workspace.FindByPath($"{CollectionsRoot}/{slug}/{KindResolver.LegacyCollectionFileName}") as CollectionFile;

    /// <summary>Returns the collection's slug (the path segment immediately under
    /// <c>collections/</c>) for the given file path, or <c>null</c> if the file isn't under
    /// <c>collections/</c>.</summary>
    public static string? SlugForFile(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null; // needs at least collections/<slug>/<file>
        if (!string.Equals(parts[0], CollectionsRoot, StringComparison.OrdinalIgnoreCase)) return null;
        return parts[1];
    }
}
