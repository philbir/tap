using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Variables;

/// <summary>
/// Which file a given cascade scope's <c>vars:</c> block lives in, for one editor context.
///
/// <para>Backs the "convert to variable" panel: the user picks a scope, and the panel names the
/// file the declaration will land in before they commit. Resolution deliberately mirrors
/// <see cref="VariableViewBuilder"/> — same collection attribution, same default-env fallback,
/// same collection-scoping test on the env — because the panel's promise is that a value
/// declared at a scope is the one <c>{{name}}</c> then resolves to. Two different answers to
/// "which env is in play" would break exactly that.</para>
/// </summary>
internal static class VariableDeclarationTargets
{
    /// <param name="Scope">Cascade tier, in the wire spelling (<c>workspace</c> … <c>request</c>).</param>
    /// <param name="Path">Workspace-relative file the declaration lands in, or null when
    /// unavailable.</param>
    /// <param name="Label">What to show for that file — the tier's own name, not the filename.</param>
    /// <param name="Unavailable">Why this scope can't be used here, or null when it can.</param>
    internal sealed record Target(string Scope, string? Path, string? Label, string? Unavailable);

    /// <summary>
    /// The four declarable tiers for this context, strongest-last, each either resolved to a
    /// file or carrying the reason it isn't offered. Always returns all four: a disabled row
    /// with a reason tells the user more than a row that silently isn't there.
    /// </summary>
    public static IReadOnlyList<Target> Resolve(
        LoadedWorkspace workspace, string? requestPath, string? collectionPath, string? envPath)
    {
        var request = requestPath is null ? null : workspace.FindByPath(requestPath) as RequestFile;
        var collection = ResolveCollection(workspace, request, collectionPath);
        var env = ResolveEnv(workspace, envPath, collection);

        return
        [
            workspace.Manifest is { } manifest
                ? new Target("workspace", manifest.RelativePath, manifest.Name ?? "workspace", null)
                : new Target("workspace", null, null, "This workspace has no manifest file."),

            collection is not null
                ? new Target("collection", collection.RelativePath, CollectionLabel(collection), null)
                : new Target("collection", null, null, collectionPath is null && request is null
                    ? "No collection is in scope here."
                    : "This request's collection has no collection file yet."),

            env is not null
                ? new Target("env", env.RelativePath, env.Name ?? Path.GetFileNameWithoutExtension(env.RelativePath), null)
                : new Target("env", null, null, "No environment is active here."),

            // A `.http` file is read and sent but never rewritten by Tap — its `@name = value`
            // lines are the portable format's, not ours, so there is no `vars:` block to
            // declare into. Offering the tier would promise a write we must not make.
            request is null
                ? new Target("request", null, null, "No request is open here.")
                : KindResolver.IsHttpFileName(HttpFragment.FilePath(request.RelativePath))
                    ? new Target("request", null, null, "A .http file's variables are its own format's — Tap does not rewrite them.")
                    : new Target("request", request.RelativePath, request.Name ?? Path.GetFileNameWithoutExtension(request.RelativePath), null),
        ];
    }

    /// <summary>The target for one scope, or null when that scope isn't available here.</summary>
    public static Target? For(
        LoadedWorkspace workspace, string scope, string? requestPath, string? collectionPath, string? envPath)
        => Resolve(workspace, requestPath, collectionPath, envPath)
            .FirstOrDefault(t => string.Equals(t.Scope, scope, StringComparison.OrdinalIgnoreCase));

    private static CollectionFile? ResolveCollection(
        LoadedWorkspace workspace, RequestFile? request, string? collectionPath)
    {
        if (collectionPath is not null) return workspace.FindByPath(collectionPath) as CollectionFile;
        return request is null ? null : CollectionLocator.ForRequest(workspace, request);
    }

    private static EnvFile? ResolveEnv(LoadedWorkspace workspace, string? envPath, CollectionFile? collection)
    {
        var env = envPath is not null
            ? workspace.FindByPath(envPath) as EnvFile
            : workspace.Manifest?.DefaultEnv is { } defaultRef
                ? workspace.Resolve(defaultRef) as EnvFile
                : null;

        // An env confined to other collections contributes nothing here, so declaring into it
        // would write a variable this context can never resolve.
        return env is not null && env.AppliesTo(SlugOf(collection)) ? env : null;
    }

    private static string CollectionLabel(CollectionFile collection)
        => collection.Name
           ?? Path.GetFileName(Path.GetDirectoryName(collection.RelativePath) ?? string.Empty);

    private static string? SlugOf(CollectionFile? collection)
        => collection is null ? null : CollectionLocator.SlugForFile(collection.RelativePath);
}
