using Tap.Execution.Agent;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Cli.Workspace;

/// <summary>
/// Chooses what a run covers: one named target, or every test set and flow carrying a tag.
///
/// <para>The rule that matters is what happens when a selection matches nothing — it is an
/// error, never an empty-but-green run. A misspelled <c>--tag</c> that silently selected zero
/// tests would leave a pipeline permanently passing while testing nothing, and there is no
/// signal anywhere in the output to notice it by.</para>
/// </summary>
public static class TargetSelector
{
    private static readonly WorkspaceKind[] Runnable = [WorkspaceKind.Test, WorkspaceKind.Flow];

    /// <summary>
    /// Every test set and flow carrying any of <paramref name="tags"/>.
    ///
    /// <para>Repeated tags union rather than intersect: "run the smoke tests and the critical
    /// tests" is what the flag reads like. It is also the safer of the two — an author who
    /// meant intersection gets more tests than they wanted, where the reverse gets none and
    /// looks like success.</para>
    /// </summary>
    public static bool TryByTags(
        LoadedWorkspace workspace,
        IReadOnlyList<string> tags,
        out IReadOnlyList<ResolvedTarget> targets,
        out string error)
    {
        targets = [];
        error = string.Empty;

        var wanted = new HashSet<string>(
            tags.Select(t => t.Trim()).Where(t => t.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            error = "--tag needs a tag name.";
            return false;
        }

        // Ordered by path so two runs of the same command report in the same order — a diff
        // between CI logs should show what changed, not how the filesystem felt today.
        var matched = workspace.Files
            .Where(f => Runnable.Contains(f.Kind))
            .Where(f => f.Tags.Any(wanted.Contains))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .Select(f => new ResolvedTarget(f, f.RelativePath))
            .ToArray();

        if (matched.Length == 0)
        {
            error = $"No test sets or flows are tagged {Quote(wanted)}.{Available(workspace)}";
            return false;
        }

        targets = matched;
        return true;
    }

    /// <summary>Every tag in use across the workspace's runnable files, for an error message
    /// that tells you what you could have typed.</summary>
    private static string Available(LoadedWorkspace workspace)
    {
        var tags = workspace.Files
            .Where(f => Runnable.Contains(f.Kind))
            .SelectMany(f => f.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tags.Length == 0
            ? " No test set or flow in this workspace carries any tag."
            : $" Tags in use: {string.Join(", ", tags)}.";
    }

    private static string Quote(IEnumerable<string> tags)
        => string.Join(" or ", tags.Select(t => $"'{t}'"));
}
