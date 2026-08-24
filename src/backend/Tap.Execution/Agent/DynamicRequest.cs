using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Agent;

/// <summary>
/// An ad-hoc request an agent wants to fire through an existing collection — inheriting the
/// collection's baseUrl, default headers, vars, and auth without ever holding any of the
/// credentials itself. Turned into an in-memory <see cref="RequestFile"/> by
/// <see cref="DynamicRequestFactory.Create"/> and rendered/executed exactly like a saved one.
/// </summary>
public sealed record DynamicRequestSpec
{
    public required string Method { get; init; }

    /// <summary>Path to hit, normally relative — it is joined onto the collection (or active
    /// env) baseUrl the same way a saved request's is. Absolute URLs are refused unless
    /// <see cref="AllowAnyUrl"/> is set; see <see cref="DynamicRequestFactory.EnsureCollectionScoped"/>.</summary>
    public required string Url { get; init; }

    /// <summary>Ordered header list — a list rather than a dictionary so a caller can send a
    /// repeated header the way HTTP allows.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }

    public string? Body { get; init; }

    /// <summary>Auth profile ref (path relative to the collection directory, or <c>id:…</c>).
    /// Null inherits the collection / env default, like a saved request without
    /// <c>auth:</c>.</summary>
    public string? Auth { get; init; }

    /// <summary>Display name; defaults to "<c>METHOD url</c>".</summary>
    public string? Name { get; init; }

    /// <summary>Opts out of the collection-scoped URL guard. Off by default deliberately:
    /// with inherited auth attached, "send my token wherever the URL says" is exactly the
    /// primitive a prompt-injected agent would reach for.</summary>
    public bool AllowAnyUrl { get; init; }
}

/// <summary>
/// Builds an in-memory <see cref="RequestFile"/> from a <see cref="DynamicRequestSpec"/> by
/// synthesizing <c>.req.tap</c> source and running it through the real <see cref="FileParser"/>
/// — the same road a saved file travels, so collection, env, auth, and variable resolution
/// cannot diverge from what a saved request would do.
/// </summary>
public static class DynamicRequestFactory
{
    /// <param name="collection">The owning collection: its workspace-relative path
    /// (<c>collections/demo/_collection.tap</c>), its directory (<c>collections/demo</c>), or
    /// its display name, matched case-insensitively.</param>
    public static RequestFile Create(LoadedWorkspace workspace, string collection, DynamicRequestSpec spec)
    {
        var owner = ResolveCollection(workspace, collection);
        AgentAccess.EnsureAllowed(owner);

        EnsureSingleLine("method", spec.Method);
        EnsureSingleLine("url", spec.Url);
        if (spec.Headers is not null)
        {
            foreach (var (name, value) in spec.Headers)
            {
                EnsureSingleLine("header name", name);
                EnsureSingleLine($"'{name}' header value", value);
            }
        }

        if (!spec.AllowAnyUrl && IsAbsolute(spec.Url))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DYNAMIC_URL_NOT_COLLECTION_SCOPED,
                $"Dynamic request URLs must be relative so they resolve against the collection's baseUrl; got '{spec.Url}'. " +
                "Sending to any URL is an explicit opt-in (AllowAnyUrl; --allow-any-url in the CLI)."));
        }

        var path = SyntheticPath(workspace, owner);
        var source = ComposeSource(spec);

        if (FileParser.Parse(path, source) is not RequestFile parsed)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID,
                "The synthesized dynamic request did not parse as a request.",
                path));
        }
        return parsed;
    }

    /// <summary>
    /// Post-render half of the URL guard, to be called on the rendered result before it is
    /// sent. The pre-render check in <see cref="Create"/> catches a literal absolute URL; this
    /// catches the indirect one — <c>{{SOME_VAR}}/x</c> where the variable expands to an
    /// absolute URL, which skips the baseUrl join entirely. If no join happened, the request
    /// is not going to the collection's host and doesn't get to carry its credentials.
    /// </summary>
    public static void EnsureCollectionScoped(ResolvedRequest rendered, bool allowAnyUrl)
    {
        if (allowAnyUrl || rendered.Metadata.ResolvedBaseUrl is not null) return;
        throw new WorkspaceParseException(new WorkspaceError(
            WorkspaceErrorCode.E_DYNAMIC_URL_NOT_COLLECTION_SCOPED,
            "The dynamic request's URL rendered to an absolute URL, bypassing the collection's baseUrl. " +
            "Sending to any URL is an explicit opt-in (AllowAnyUrl; --allow-any-url in the CLI)."));
    }

    private static CollectionFile ResolveCollection(LoadedWorkspace workspace, string collection)
    {
        var wanted = collection.Replace('\\', '/').TrimEnd('/');
        var matches = workspace.Collections
            .Where(c =>
                string.Equals(c.RelativePath, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(c.RelativePath)?.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches switch
        {
            [var single] => single,
            [] => throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID,
                $"No collection matches '{collection}'. Available: [{string.Join(", ", workspace.Collections.Select(c => c.Name ?? c.RelativePath))}]")),
            _ => throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID,
                $"'{collection}' matches more than one collection: [{string.Join(", ", matches.Select(c => c.RelativePath))}]. Use the collection path.")),
        };
    }

    /// <summary>A path inside the collection's directory — placement is what makes
    /// <see cref="Tap.Workspace.Rendering.CollectionLocator"/> attribute the request to the
    /// collection — that provably isn't a real file, so nothing on disk can be shadowed.</summary>
    private static string SyntheticPath(LoadedWorkspace workspace, CollectionFile owner)
    {
        var dir = Path.GetDirectoryName(owner.RelativePath)?.Replace('\\', '/') ?? string.Empty;
        var path = $"{dir}/_dynamic{KindResolver.SuffixFor(WorkspaceKind.Request)}";
        for (var n = 2; workspace.FindByPath(path) is not null; n++)
        {
            path = $"{dir}/_dynamic-{n}{KindResolver.SuffixFor(WorkspaceKind.Request)}";
        }
        return path;
    }

    private static string ComposeSource(DynamicRequestSpec spec)
    {
        var block = new System.Text.StringBuilder();
        block.Append(spec.Method.Trim()).Append(' ').Append(spec.Url.Trim()).Append('\n');
        if (spec.Headers is not null)
        {
            foreach (var (name, value) in spec.Headers)
            {
                block.Append(name.Trim()).Append(": ").Append(value).Append('\n');
            }
        }
        if (!string.IsNullOrEmpty(spec.Body))
        {
            block.Append('\n').Append(spec.Body).Append('\n');
        }

        // The fence must be longer than any backtick run inside the block, or a body that
        // happens to contain ``` would end the block early and smuggle the rest outside it.
        var fence = new string('`', Math.Max(3, LongestBacktickRun(block.ToString()) + 1));

        var source = new System.Text.StringBuilder();
        source.Append("---\n");
        source.Append("kind: request\n");
        source.Append("name: ").Append(YamlQuote(spec.Name ?? $"{spec.Method.Trim()} {spec.Url.Trim()}")).Append('\n');
        if (!string.IsNullOrWhiteSpace(spec.Auth))
        {
            source.Append("auth: ").Append(YamlQuote(spec.Auth)).Append('\n');
        }
        source.Append("---\n\n");
        source.Append(fence).Append("http\n");
        source.Append(block);
        source.Append(fence).Append('\n');
        return source.ToString();
    }

    private static void EnsureSingleLine(string what, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DYNAMIC_REQUEST_INVALID,
                $"The dynamic request's {what} must be a non-empty single line."));
        }
    }

    private static bool IsAbsolute(string url)
    {
        var trimmed = url.TrimStart();
        // "//host" counts: the renderer's scheme normalization turns it into an absolute URL.
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, current = 0;
        foreach (var ch in text)
        {
            if (ch == '`') longest = Math.Max(longest, ++current);
            else current = 0;
        }
        return longest;
    }

    private static string YamlQuote(string value)
        => "'" + value.Replace("'", "''") + "'";
}
