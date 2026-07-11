using Tap.Workspace;

namespace Tap.Studio;

/// <summary>
/// Single resolver for any workspace-relative path crossing an API boundary.
///
/// <para>Every read, write, move, and delete that consumes a caller-supplied path must route
/// through here so the same rejection rules apply everywhere:</para>
/// <list type="bullet">
///   <item>Path must be non-empty after trimming and slash normalization.</item>
///   <item>No segment may be empty, <c>.</c>, or <c>..</c>.</item>
///   <item>The input must not be rooted (absolute, drive-rooted, or UNC).</item>
///   <item>The canonical full path must stay strictly under the selected workspace root.</item>
/// </list>
/// <para>The encoded-traversal case (e.g. <c>a/%2e%2e/x.req.md</c>) is not handled here —
/// callers must hand in already URL-decoded paths. ASP.NET Core decodes route + query values
/// before they reach us, and JSON bodies are not URL-encoded to begin with, so the only place
/// a literal <c>%2e</c> can appear is as itself in a filename, which the segment check still
/// allows. This matches the original behavior of <c>TryResolveWorkspacePath</c>.</para>
/// </summary>
internal static class WorkspacePathResolver
{
    public static bool TryResolve(string rootDirectory, string? relative,
        out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "Path is required.";
            return false;
        }

        // Reject rooted inputs before normalization so a Windows absolute path like
        // "C:\foo" is caught regardless of separator orientation.
        if (Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
        {
            error = "Path must be workspace-relative.";
            return false;
        }

        var normalized = relative.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            error = "Path is required.";
            return false;
        }
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || string.IsNullOrWhiteSpace(segment))
            {
                error = "Path may not contain empty, '.', or '..' segments.";
                return false;
            }
        }

        var tapDir = rootDirectory;
        var combined = Path.GetFullPath(Path.Combine(tapDir, normalized));
        var tapDirFull = Path.GetFullPath(tapDir).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // OrdinalIgnoreCase matches macOS HFS+/APFS and Windows NTFS default semantics.
        // Linux case-sensitive FSes still benefit because a same-case prefix check is a
        // strict subset of case-insensitive.
        if (!combined.StartsWith(tapDirFull, StringComparison.OrdinalIgnoreCase))
        {
            error = "Path escapes the workspace.";
            return false;
        }
        fullPath = combined;
        return true;
    }

    public static bool TryResolve(WorkspaceService svc, string? relative,
        out string fullPath, out string error)
        => TryResolve(svc.RootDirectory, relative, out fullPath, out error);

    /// <summary>Returns the workspace content directory for a selected root.</summary>
    public static string TapDirectory(string rootDirectory)
        => rootDirectory;
}
