namespace Tap.Studio.Cli.Workspace;

/// <summary>
/// Finds the workspace a command should act on: the one the user named, the nearest one
/// above the working directory, or — failing both — the first one beneath it.
///
/// <para>Walking up mirrors what every other repo-scoped tool does — <c>git</c>, <c>dotnet</c>,
/// <c>npm</c> — so <c>tap-studio test</c> works from anywhere inside a checkout without a flag.
/// The downward fallback covers the other common seat: standing at a repo root whose
/// workspace lives in a subfolder (<c>samples/sample-workspace</c>, <c>api/tap</c>). The scan
/// is breadth-first in ordinal order, so "the first one" is deterministic — the shallowest,
/// then alphabetically first — and it is bounded (depth cap, dot- and dependency-directory
/// skips) so a giant tree doesn't turn a typo'd command into a filesystem crawl. Both
/// layouts are recognised: a folder holding <c>workspace.tap</c> directly, and the older
/// <c>.tap/</c> subfolder. The pre-0.7.0 manifest name (<c>workspace.tap</c>) is accepted for as long
/// as the loader reads the legacy extension family.</para>
/// </summary>
public static class WorkspaceLocator
{
    public const string ManifestFileName = Tap.Workspace.WorkspaceLoader.ManifestFileName;
    public const string TapDirectoryName = ".tap";

    /// <summary>How deep the downward fallback looks. Workspaces live near the top of a
    /// repo; anything deeper is more likely test data than the thing the user meant.</summary>
    private const int MaxScanDepth = 5;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "out", "wwwroot",
    };

    /// <summary>
    /// Resolves the workspace root, or returns null with a reason the caller can print.
    /// </summary>
    /// <param name="explicitRoot">What <c>--workspace</c> named, if anything.</param>
    /// <param name="startDirectory">Where to start walking up. Defaults to the process's
    /// working directory.</param>
    public static bool TryLocate(string? explicitRoot, string? startDirectory, out string root, out string error)
    {
        root = string.Empty;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var named = Path.GetFullPath(explicitRoot);
            if (!Directory.Exists(named))
            {
                error = $"'{explicitRoot}' is not a directory.";
                return false;
            }
            if (TryManifestRoot(named, out root)) return true;

            error = $"'{named}' does not contain a {ManifestFileName} (nor a {TapDirectoryName}/{ManifestFileName}).";
            return false;
        }

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory()));
        var searched = current.FullName;
        while (current is not null)
        {
            if (TryManifestRoot(current.FullName, out root)) return true;
            current = current.Parent;
        }

        if (TryFindBeneath(searched, out root)) return true;

        error = $"No Tap workspace found in '{searched}', any parent directory, or the first "
              + $"{MaxScanDepth} levels beneath it. Point at one with --workspace, or run from "
              + $"inside a folder containing {ManifestFileName}.";
        return false;
    }

    /// <summary>The downward fallback: breadth-first so a shallow workspace always beats a
    /// deep one, ordinal order within a level so the pick is stable across runs and
    /// machines. The start directory itself was already ruled out by the upward walk.</summary>
    private static bool TryFindBeneath(string start, out string root)
    {
        root = string.Empty;
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            if (depth > 0 && TryManifestRoot(directory, out root)) return true;
            if (depth == MaxScanDepth) continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(children, StringComparer.Ordinal);
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || SkippedDirectories.Contains(name)) continue;
                queue.Enqueue((child, depth + 1));
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="directory"/> is (or contains) a workspace root.</summary>
    public static bool TryManifestRoot(string directory, out string root)
    {
        if (Tap.Workspace.WorkspaceLoader.HasManifest(directory))
        {
            root = directory;
            return true;
        }

        var nested = Path.Combine(directory, TapDirectoryName);
        if (Tap.Workspace.WorkspaceLoader.HasManifest(nested))
        {
            root = nested;
            return true;
        }

        root = string.Empty;
        return false;
    }
}
