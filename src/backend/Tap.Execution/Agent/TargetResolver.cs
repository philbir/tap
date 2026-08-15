using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Execution.Agent;

/// <summary>What a command was asked to act on, once a name has been turned into a file.</summary>
/// <param name="File">The resolved workspace file.</param>
/// <param name="Path">Its workspace-relative path.</param>
public readonly record struct ResolvedTarget(WorkspaceFile File, string Path);

/// <summary>
/// Turns the <c>&lt;name|path&gt;</c> argument into a workspace file.
///
/// <para>Three ways to say the same thing, tried in order: the workspace-relative path, the
/// <c>name:</c> in the frontmatter, then the filename stem. A path is unambiguous and scripts
/// should use it; a display name is what someone reads off the Testing tab and types from
/// memory, and refusing that would make the CLI feel like a different product from the UI.</para>
///
/// <para>An ambiguous name is an error listing the candidates, never a guess — picking one of
/// two same-named test sets silently is how a green pipeline ends up not running what
/// everybody assumes it runs.</para>
/// </summary>
public static class TargetResolver
{
    /// <summary>Resolves against the files matching <paramref name="kinds"/>.</summary>
    public static bool TryResolve(
        LoadedWorkspace workspace,
        string query,
        IReadOnlyCollection<WorkspaceKind> kinds,
        out ResolvedTarget target,
        out string error)
    {
        target = default;
        error = string.Empty;

        var candidates = workspace.Files.Where(f => kinds.Contains(f.Kind)).ToArray();
        if (candidates.Length == 0)
        {
            error = $"This workspace contains no {Describe(kinds)}.";
            return false;
        }

        var normalized = query.Trim().Replace('\\', '/');

        // 1. Exact workspace-relative path.
        if (workspace.FindByPath(normalized) is { } byPath && kinds.Contains(byPath.Kind))
        {
            target = new ResolvedTarget(byPath, byPath.RelativePath);
            return true;
        }

        // 2. Display name, then 3. filename stem. Both case-insensitive — a name typed from
        // memory rarely matches capitalisation, and there is no value in being strict.
        if (TryUnique(candidates.Where(f => Matches(f.Name, normalized)), out target, out error, normalized)) return true;
        if (error.Length > 0) return false;

        if (TryUnique(candidates.Where(f => Matches(Stem(f.RelativePath), normalized)), out target, out error, normalized)) return true;
        if (error.Length > 0) return false;

        error = $"No {Describe(kinds)} match '{query}'.{Suggest(candidates, normalized)}";
        return false;
    }

    private static bool TryUnique(
        IEnumerable<WorkspaceFile> matches, out ResolvedTarget target, out string error, string query)
    {
        target = default;
        error = string.Empty;

        var hits = matches.ToArray();
        if (hits.Length == 1)
        {
            target = new ResolvedTarget(hits[0], hits[0].RelativePath);
            return true;
        }
        if (hits.Length > 1)
        {
            error = $"'{query}' is ambiguous — {hits.Length} files match:\n"
                  + string.Join('\n', hits.Select(h => $"  {h.RelativePath}"))
                  + "\nUse the path instead.";
        }
        return false;
    }

    /// <summary>Up to five near misses, so a typo costs one line rather than a directory listing.</summary>
    private static string Suggest(IReadOnlyList<WorkspaceFile> candidates, string query)
    {
        var near = candidates
            .Where(c => (c.Name ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
                     || c.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToArray();

        var listed = near.Length > 0 ? near : candidates.Take(5).ToArray();
        if (listed.Length == 0) return string.Empty;

        var header = near.Length > 0 ? "\nDid you mean:" : "\nAvailable:";
        return header + "\n" + string.Join('\n', listed.Select(c => $"  {Label(c)}"));
    }

    private static string Label(WorkspaceFile file)
        => string.IsNullOrWhiteSpace(file.Name)
            ? file.RelativePath
            : $"{file.Name}  ({file.RelativePath})";

    private static bool Matches(string? value, string query)
        => !string.IsNullOrWhiteSpace(value)
        && string.Equals(value.Trim(), query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Filename stem without the two-part suffix — <c>orders.test.md</c> → <c>orders</c>.</summary>
    public static string Stem(string relativePath)
    {
        var file = Path.GetFileName(relativePath);
        var cut = file.IndexOf('.');
        return cut <= 0 ? file : file[..cut];
    }

    private static string Describe(IReadOnlyCollection<WorkspaceKind> kinds)
    {
        var names = kinds.Select(k => k switch
        {
            WorkspaceKind.Test => "test set",
            WorkspaceKind.Flow => "flow",
            WorkspaceKind.Request => "request",
            _ => k.ToString().ToLowerInvariant(),
        }).ToArray();

        return names.Length switch
        {
            1 => names[0] + "s",
            2 => $"{names[0]}s or {names[1]}s",
            _ => string.Join(", ", names.Select(n => n + "s")),
        };
    }
}
