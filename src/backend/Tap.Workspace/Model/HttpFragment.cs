namespace Tap.Workspace.Model;

/// <summary>
/// Fragment paths — how a request inside a multi-request <c>.http</c> file is addressed:
/// <c>collections/demo/orders.http#get-order</c>.
///
/// <para>Every other workspace file is one file, one <see cref="WorkspaceFile"/>. A <c>.http</c>
/// file holds several requests, so the path alone can't identify one. The fragment is appended to
/// the filename segment (never to a directory), which keeps every path operation that splits on
/// <c>/</c> working unchanged — collection attribution walks directories, and those still read
/// exactly as before.</para>
///
/// <para>The fragment is the <em>canonical</em> identity: it stays stable when requests are added
/// to or removed from the file, which an ordinal would not. Resolving a bare file path is a
/// convenience the resolver offers when the file holds exactly one request.</para>
/// </summary>
public static class HttpFragment
{
    public const char Separator = '#';

    /// <summary>Appends a request's name to its file path.</summary>
    public static string Compose(string relativePath, string requestName)
        => $"{relativePath}{Separator}{Slugify(requestName)}";

    /// <summary>Splits a possibly-fragmented path into its file part and fragment. The fragment
    /// is null for ordinary paths.</summary>
    public static (string Path, string? Fragment) Split(string relativePath)
    {
        var cut = relativePath.IndexOf(Separator);
        return cut < 0
            ? (relativePath, null)
            : (relativePath[..cut], relativePath[(cut + 1)..]);
    }

    /// <summary>The file part of a path, dropping any fragment. Safe on paths that have none.</summary>
    public static string FilePath(string relativePath) => Split(relativePath).Path;

    public static bool HasFragment(string relativePath) => relativePath.Contains(Separator);

    /// <summary>
    /// Normalizes a request name into a fragment token: lowercase, non-alphanumerics collapsed to
    /// dashes. A fragment goes into URLs and command-line arguments, so it has to survive both
    /// without quoting — "Get Order (v2)" addressed as <c>#get-order-v2</c>.
    /// </summary>
    public static string Slugify(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
