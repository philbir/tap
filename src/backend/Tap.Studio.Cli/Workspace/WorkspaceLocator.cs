namespace Tap.Studio.Cli.Workspace;

/// <summary>
/// Finds the workspace a command should act on: the one the user named, or the nearest one
/// above the working directory.
///
/// <para>Walking up mirrors what every other repo-scoped tool does — <c>git</c>, <c>dotnet</c>,
/// <c>npm</c> — so <c>tap-studio test</c> works from anywhere inside a checkout without a flag.
/// Both layouts are recognised: a folder holding <c>tap.md</c> directly, and the older
/// <c>.tap/</c> subfolder.</para>
/// </summary>
public static class WorkspaceLocator
{
    public const string ManifestFileName = "tap.md";
    public const string TapDirectoryName = ".tap";

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

        error = $"No Tap workspace found in '{searched}' or any parent directory. "
              + $"Point at one with --workspace, or run from inside a folder containing {ManifestFileName}.";
        return false;
    }

    /// <summary>True when <paramref name="directory"/> is (or contains) a workspace root.</summary>
    public static bool TryManifestRoot(string directory, out string root)
    {
        if (File.Exists(Path.Combine(directory, ManifestFileName)))
        {
            root = directory;
            return true;
        }

        var nested = Path.Combine(directory, TapDirectoryName);
        if (File.Exists(Path.Combine(nested, ManifestFileName)))
        {
            root = nested;
            return true;
        }

        root = string.Empty;
        return false;
    }
}
