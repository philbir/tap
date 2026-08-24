using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tap.Studio.Cli.Agent;

public enum AgentEnv { Claude, Codex, Copilot, OpenCode }

public enum InstallScope { Project, User }

/// <summary>What one <c>agent init</c> run did: files written or updated, and the steps it
/// deliberately left to the user (config files owned by another app that shouldn't be
/// edited behind its back).</summary>
public sealed class InstallReport
{
    public List<string> Actions { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Manual { get; } = [];
}

/// <summary>
/// Installs agent support — the skills and the MCP registration — into the places each
/// agent environment actually reads. Two rules shape every branch:
///
/// <para><b>Idempotent by construction.</b> Re-running updates our own files in place:
/// skill files are overwritten (that is how updates ship), JSON/TOML registrations are
/// added once and left alone when present (<c>force</c> replaces ours), and instruction
/// files get one marker-fenced block that is replaced, never duplicated.</para>
///
/// <para><b>Never edit another app's private state.</b> Project-scope files are all
/// checked-in conventions (.mcp.json, .vscode/mcp.json, opencode.json, AGENTS.md); the
/// user-scope ones that aren't stable documented formats (Claude's ~/.claude.json,
/// Copilot's user profile) get a printed instruction instead of a write.</para>
/// </summary>
public sealed class AgentInstaller(string projectDir, string home)
{
    private const string ServerName = "tap-studio";
    private const string BlockStart = "<!-- tap-agent:start -->";
    private const string BlockEnd = "<!-- tap-agent:end -->";

    public InstallReport Install(
        AgentEnv env, InstallScope scope, bool skills, bool mcp, string? workspaceArg, bool force)
    {
        var report = new InstallReport();
        if (skills) InstallSkills(env, scope, report);
        if (mcp) InstallMcp(env, scope, workspaceArg, force, report);
        return report;
    }

    // -------------------------------------------------------------------------------------
    // Skills
    // -------------------------------------------------------------------------------------

    private void InstallSkills(AgentEnv env, InstallScope scope, InstallReport report)
    {
        if (env == AgentEnv.Claude)
        {
            // Claude Code has a native skills system; install into it and we're done.
            var root = scope == InstallScope.Project
                ? Path.Combine(projectDir, ".claude", "skills")
                : Path.Combine(home, ".claude", "skills");
            WriteSkillFiles(root, report);
            return;
        }

        if (env == AgentEnv.Copilot && scope == InstallScope.User)
        {
            report.Manual.Add(
                "Copilot has no stable user-level instructions file. Install at project scope "
                + "(--scope project writes .github/copilot-instructions.md), or add the guide "
                + "paths to your personal instructions by hand.");
            return;
        }

        // No native skills system: write the files to the cross-agent convention
        // (.agents/skills/<name>/SKILL.md) and leave one managed, marker-fenced pointer
        // block in the instructions file that environment reads.
        var skillsRoot = scope == InstallScope.Project
            ? Path.Combine(projectDir, ".agents", "skills")
            : Path.Combine(home, ".agents", "skills");
        WriteSkillFiles(skillsRoot, report);
        RemoveLegacySkills(scope, report);

        var instructionsPath = (env, scope) switch
        {
            (AgentEnv.Codex, InstallScope.Project) => Path.Combine(projectDir, "AGENTS.md"),
            (AgentEnv.Codex, InstallScope.User) => Path.Combine(home, ".codex", "AGENTS.md"),
            (AgentEnv.OpenCode, InstallScope.Project) => Path.Combine(projectDir, "AGENTS.md"),
            (AgentEnv.OpenCode, InstallScope.User) => Path.Combine(home, ".config", "opencode", "AGENTS.md"),
            (AgentEnv.Copilot, InstallScope.Project) => Path.Combine(projectDir, ".github", "copilot-instructions.md"),
            _ => throw new InvalidOperationException($"No instructions path for {env}/{scope}."),
        };

        UpsertPointerBlock(instructionsPath, PointerBlock(skillsRoot, scope), report);
    }

    private void WriteSkillFiles(string root, InstallReport report)
    {
        var count = 0;
        foreach (var (relativePath, content) in AgentAssets.Skills())
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            count++;
        }
        report.Actions.Add($"skills → {Display(root)} ({count} files: tap-studio, tap-author)");
    }

    /// <summary>Before 0.7.1 the neutral skills went to <c>.tap/agent/</c>; they now go to
    /// the cross-agent <c>.agents/skills/</c> convention. Re-running has to clear the old
    /// copy, or a stale second set of guides sits there for an agent to find. Only the skill
    /// directories this tool wrote are removed — anything else under <c>.tap/</c> is somebody
    /// else's, and the parent directories go only if emptied by the removal.</summary>
    private void RemoveLegacySkills(InstallScope scope, InstallReport report)
    {
        var legacyRoot = scope == InstallScope.Project
            ? Path.Combine(projectDir, ".tap", "agent")
            : Path.Combine(home, ".tap", "agent");
        if (!Directory.Exists(legacyRoot)) return;

        var removed = 0;
        foreach (var skill in AgentAssets.Skills()
            .Select(f => f.RelativePath.Split('/')[0]).Distinct(StringComparer.Ordinal))
        {
            var path = Path.Combine(legacyRoot, skill);
            if (!Directory.Exists(path)) continue;
            Directory.Delete(path, recursive: true);
            removed++;
        }

        if (removed == 0) return;
        report.Actions.Add($"removed legacy skills ← {Display(legacyRoot)} ({removed} skill directories)");

        // Only prune upwards while our removal is what emptied the directory.
        for (var dir = legacyRoot; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
            Directory.Delete(dir);
            if (Path.GetFileName(dir) == ".tap") break;
        }
    }

    /// <summary>The block dropped into an instructions file. Paths are relative for project
    /// scope (the file is checked in and must work on every machine) and absolute for user
    /// scope (the file is personal and cwd is unknowable).</summary>
    private string PointerBlock(string skillsRoot, InstallScope scope)
    {
        var root = scope == InstallScope.Project
            ? Path.GetRelativePath(projectDir, skillsRoot).Replace(Path.DirectorySeparatorChar, '/')
            : skillsRoot.Replace(Path.DirectorySeparatorChar, '/');
        return $"""
            {BlockStart}
            ## Tap workspace — agent guides

            This project contains a Tap API workspace (markdown request/collection/auth files
            run by the `tap-studio` CLI or its MCP tools, with auth handled by the workspace).

            - Before running requests against it, read `{root}/tap-studio/SKILL.md`.
            - Before creating or editing workspace files (`*.req.tap`, `_collection.tap`,
              `*.auth.tap`, `*.env.tap`, `*.flow.tap`, `*.test.tap`), read
              `{root}/tap-author/SKILL.md` and the files under `{root}/tap-author/references/`.
            {BlockEnd}
            """;
    }

    private void UpsertPointerBlock(string path, string block, InstallReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, block + "\n");
            report.Actions.Add($"instructions → {Display(path)} (created with Tap guide block)");
            return;
        }

        var text = File.ReadAllText(path);
        var start = text.IndexOf(BlockStart, StringComparison.Ordinal);
        var end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            text = text[..start] + block + text[(end + BlockEnd.Length)..];
            File.WriteAllText(path, text);
            report.Actions.Add($"instructions → {Display(path)} (Tap guide block updated)");
        }
        else
        {
            File.WriteAllText(path, text.TrimEnd() + "\n\n" + block + "\n");
            report.Actions.Add($"instructions → {Display(path)} (Tap guide block appended)");
        }
    }

    // -------------------------------------------------------------------------------------
    // MCP registration
    // -------------------------------------------------------------------------------------

    private void InstallMcp(
        AgentEnv env, InstallScope scope, string? workspaceArg, bool force, InstallReport report)
    {
        switch (env, scope)
        {
            case (AgentEnv.Claude, InstallScope.Project):
                MergeJsonServer(
                    Path.Combine(projectDir, ".mcp.json"), rootKey: "mcpServers",
                    ClaudeStyleEntry(workspaceArg), force, report);
                break;

            case (AgentEnv.Claude, InstallScope.User):
                // ~/.claude.json carries far more than server registrations; Claude's own
                // CLI is the safe writer for it.
                report.Manual.Add("run: claude mcp add --scope user tap-studio -- tap-studio mcp");
                break;

            case (AgentEnv.Copilot, InstallScope.Project):
                MergeJsonServer(
                    Path.Combine(projectDir, ".vscode", "mcp.json"), rootKey: "servers",
                    CopilotEntry(workspaceArg), force, report);
                break;

            case (AgentEnv.Copilot, InstallScope.User):
                report.Manual.Add(
                    "in VS Code run the 'MCP: Open User Configuration' command and add "
                    + $"{{ \"{ServerName}\": {{ \"command\": \"tap-studio\", \"args\": [\"mcp\"] }} }} under \"servers\".");
                break;

            case (AgentEnv.OpenCode, _):
                MergeJsonServer(
                    scope == InstallScope.Project
                        ? Path.Combine(projectDir, "opencode.json")
                        : Path.Combine(home, ".config", "opencode", "opencode.json"),
                    rootKey: "mcp", OpenCodeEntry(scope == InstallScope.Project ? workspaceArg : null),
                    force, report);
                break;

            case (AgentEnv.Codex, _):
                AppendTomlServer(
                    scope == InstallScope.Project
                        ? Path.Combine(projectDir, ".codex", "config.toml")
                        : Path.Combine(home, ".codex", "config.toml"),
                    scope == InstallScope.Project ? workspaceArg : null, force, report);
                break;
        }
    }

    private static JsonObject ClaudeStyleEntry(string? workspaceArg) => new()
    {
        ["command"] = "tap-studio",
        ["args"] = ArgsArray(workspaceArg),
    };

    private static JsonObject CopilotEntry(string? workspaceArg) => new()
    {
        ["type"] = "stdio",
        ["command"] = "tap-studio",
        ["args"] = ArgsArray(workspaceArg),
    };

    private static JsonObject OpenCodeEntry(string? workspaceArg)
    {
        var command = new JsonArray("tap-studio", "mcp");
        if (workspaceArg is not null)
        {
            command.Add("--workspace");
            command.Add(workspaceArg);
        }
        return new JsonObject { ["type"] = "local", ["command"] = command, ["enabled"] = true };
    }

    private static JsonArray ArgsArray(string? workspaceArg)
        => workspaceArg is null ? new JsonArray("mcp") : new JsonArray("mcp", "--workspace", workspaceArg);

    private void MergeJsonServer(
        string path, string rootKey, JsonObject entry, bool force, InstallReport report)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException($"'{Display(path)}' is not a JSON object; fix or remove it first.");
        }
        else
        {
            root = [];
        }

        if (root[rootKey] is not JsonObject servers)
        {
            servers = [];
            root[rootKey] = servers;
        }

        if (servers[ServerName] is not null && !force)
        {
            report.Skipped.Add($"{Display(path)} already registers '{ServerName}' — left as-is (--force replaces it)");
            return;
        }

        servers[ServerName] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        report.Actions.Add($"mcp → {Display(path)} ('{ServerName}')");
    }

    /// <summary>TOML is append-only here on purpose: parsing and rewriting somebody's
    /// config.toml without a TOML library risks mangling what we didn't understand. An
    /// existing <c>[mcp_servers.tap-studio]</c> section is left alone (or flagged with
    /// <paramref name="force"/> as a manual edit — we won't try to splice it out).</summary>
    private void AppendTomlServer(string path, string? workspaceArg, bool force, InstallReport report)
    {
        var header = $"[mcp_servers.{ServerName}]";
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        if (existing.Contains(header, StringComparison.Ordinal))
        {
            if (force)
            {
                report.Manual.Add(
                    $"{Display(path)} already has {header} — edit that section by hand; "
                    + "this tool won't rewrite TOML it didn't author.");
            }
            else
            {
                report.Skipped.Add($"{Display(path)} already registers '{ServerName}' — left as-is");
            }
            return;
        }

        var args = workspaceArg is null
            ? "[\"mcp\"]"
            : $"[\"mcp\", \"--workspace\", \"{workspaceArg}\"]";
        var section = new StringBuilder();
        if (existing.Length > 0 && !existing.EndsWith('\n')) section.Append('\n');
        if (existing.Length > 0) section.Append('\n');
        section.Append(header).Append('\n');
        section.Append("command = \"tap-studio\"").Append('\n');
        section.Append("args = ").Append(args).Append('\n');

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, section.ToString());
        report.Actions.Add($"mcp → {Display(path)} ('{ServerName}')");
    }

    private string Display(string path)
    {
        var relative = Path.GetRelativePath(projectDir, path);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        return path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..].Replace(Path.DirectorySeparatorChar, '/')
            : path;
    }
}
