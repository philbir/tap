namespace Tap.Studio.Cli.Agent;

/// <summary>
/// Figures out which agent environments this machine (and this project) plausibly has, so
/// the <c>agent init</c> wizard can preselect the right ones instead of quizzing the user
/// about tools they don't run. Evidence, not proof: a CLI on PATH or a config directory
/// someone once created. The wizard shows the evidence next to the choice and lets the
/// user overrule it either way — detection ranks the menu, it never decides.
/// </summary>
public static class AgentEnvironmentDetector
{
    /// <summary>Detected environments mapped to the one piece of evidence shown in the
    /// wizard. Absent key = nothing found.</summary>
    public static IReadOnlyDictionary<AgentEnv, string> Detect(
        string projectDir, string home, IReadOnlyList<string>? pathDirectories = null)
    {
        var path = pathDirectories ?? SplitPath();
        var found = new Dictionary<AgentEnv, string>();

        Probe(found, AgentEnv.Claude,
            () => OnPath(path, "claude") ? "claude CLI on PATH" : null,
            () => Directory.Exists(Path.Combine(home, ".claude")) ? "~/.claude exists" : null,
            () => Directory.Exists(Path.Combine(projectDir, ".claude")) ? ".claude/ in project" : null,
            () => File.Exists(Path.Combine(projectDir, ".mcp.json")) ? ".mcp.json in project" : null);

        Probe(found, AgentEnv.Codex,
            () => OnPath(path, "codex") ? "codex CLI on PATH" : null,
            () => Directory.Exists(Path.Combine(home, ".codex")) ? "~/.codex exists" : null);

        Probe(found, AgentEnv.Copilot,
            () => OnPath(path, "code") ? "VS Code CLI on PATH" : null,
            () => Directory.Exists(Path.Combine(projectDir, ".vscode")) ? ".vscode/ in project" : null,
            () => OperatingSystem.IsMacOS() && Directory.Exists("/Applications/Visual Studio Code.app")
                ? "VS Code installed" : null,
            () => Directory.Exists(Path.Combine(home, ".vscode")) ? "~/.vscode exists" : null);

        Probe(found, AgentEnv.OpenCode,
            () => OnPath(path, "opencode") ? "opencode CLI on PATH" : null,
            () => Directory.Exists(Path.Combine(home, ".config", "opencode")) ? "~/.config/opencode exists" : null);

        return found;
    }

    private static void Probe(
        Dictionary<AgentEnv, string> found, AgentEnv env, params Func<string?>[] signals)
    {
        foreach (var signal in signals)
        {
            if (signal() is { } evidence)
            {
                found[env] = evidence;
                return;
            }
        }
    }

    private static bool OnPath(IReadOnlyList<string> pathDirectories, string name)
    {
        foreach (var dir in pathDirectories)
        {
            if (dir.Length == 0) continue;
            if (File.Exists(Path.Combine(dir, name))) return true;
            if (OperatingSystem.IsWindows()
                && (File.Exists(Path.Combine(dir, name + ".exe"))
                    || File.Exists(Path.Combine(dir, name + ".cmd"))))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] SplitPath()
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
