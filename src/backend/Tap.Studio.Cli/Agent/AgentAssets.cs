using System.Reflection;

namespace Tap.Studio.Cli.Agent;

/// <summary>
/// The agent skills, read out of this assembly. They are embedded from the repo's
/// <c>.claude/skills</c> at build time (see the csproj), so the installed tool always
/// carries the same skill content the repo itself dogfoods — <c>agent init</c> writing a
/// stale copy would be worse than writing none.
/// </summary>
public static class AgentAssets
{
    private const string Prefix = "agent-skills/";

    /// <summary>Every embedded skill file as (workspace-relative path under a skills root,
    /// content) — e.g. <c>("tap-author/references/assertions.md", "…")</c>.</summary>
    public static IReadOnlyList<(string RelativePath, string Content)> Skills()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // RecursiveDir emits backslashes on Windows builds; normalize so the
            // logical layout is identical whatever OS produced the package.
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            files.Add((normalized[Prefix.Length..], reader.ReadToEnd()));
        }
        return files.OrderBy(f => f.Item1, StringComparer.Ordinal).ToArray();
    }
}
