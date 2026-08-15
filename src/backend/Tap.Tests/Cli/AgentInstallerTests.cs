using System.Text.Json;
using Tap.Studio.Cli.Agent;

namespace Tap.Tests.Cli;

/// <summary>
/// <c>agent init</c>'s file mechanics against real temp directories: each environment's
/// config format, and the two idempotency promises — registrations are added once, and
/// the instructions block is replaced, never duplicated.
/// </summary>
public sealed class AgentInstallerTests : IDisposable
{
    private readonly string _project;
    private readonly string _home;
    private readonly AgentInstaller _installer;

    public AgentInstallerTests()
    {
        _project = Directory.CreateTempSubdirectory("tap-agent-proj-").FullName;
        _home = Directory.CreateTempSubdirectory("tap-agent-home-").FullName;
        _installer = new AgentInstaller(_project, _home);
    }

    public void Dispose()
    {
        Directory.Delete(_project, recursive: true);
        Directory.Delete(_home, recursive: true);
    }

    private string ProjectFile(string relative) => Path.Combine(_project, relative);
    private static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    [Fact]
    public void The_embedded_skills_are_present_and_complete()
    {
        var skills = AgentAssets.Skills();
        Assert.Contains(skills, s => s.RelativePath == "tap-studio/SKILL.md");
        Assert.Contains(skills, s => s.RelativePath == "tap-author/SKILL.md");
        Assert.Contains(skills, s => s.RelativePath == "tap-author/references/file-formats.md");
        Assert.Contains(skills, s => s.RelativePath == "tap-author/references/assertions.md");
        Assert.All(skills, s => Assert.False(string.IsNullOrWhiteSpace(s.Content)));
    }

    [Fact]
    public void Claude_project_installs_native_skills_and_merges_mcp_json()
    {
        // A pre-existing server must survive the merge untouched.
        File.WriteAllText(ProjectFile(".mcp.json"),
            """{ "mcpServers": { "context7": { "command": "npx", "args": ["-y", "ctx"] } } }""");

        var report = _installer.Install(
            AgentEnv.Claude, InstallScope.Project, skills: true, mcp: true, workspaceArg: ".", force: false);

        Assert.True(File.Exists(ProjectFile(".claude/skills/tap-studio/SKILL.md")));
        Assert.True(File.Exists(ProjectFile(".claude/skills/tap-author/references/assertions.md")));

        var servers = Json(ProjectFile(".mcp.json")).GetProperty("mcpServers");
        Assert.Equal("npx", servers.GetProperty("context7").GetProperty("command").GetString());
        var tap = servers.GetProperty("tap-studio");
        Assert.Equal("tap-studio", tap.GetProperty("command").GetString());
        Assert.Equal(new[] { "mcp", "--workspace", "." },
            tap.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Empty(report.Manual);
    }

    [Fact]
    public void An_existing_registration_is_kept_unless_forced()
    {
        File.WriteAllText(ProjectFile(".mcp.json"),
            """{ "mcpServers": { "tap-studio": { "command": "custom" } } }""");

        var kept = _installer.Install(
            AgentEnv.Claude, InstallScope.Project, skills: false, mcp: true, workspaceArg: ".", force: false);
        Assert.Single(kept.Skipped);
        Assert.Equal("custom",
            Json(ProjectFile(".mcp.json")).GetProperty("mcpServers").GetProperty("tap-studio")
                .GetProperty("command").GetString());

        var forced = _installer.Install(
            AgentEnv.Claude, InstallScope.Project, skills: false, mcp: true, workspaceArg: ".", force: true);
        Assert.Single(forced.Actions);
        Assert.Equal("tap-studio",
            Json(ProjectFile(".mcp.json")).GetProperty("mcpServers").GetProperty("tap-studio")
                .GetProperty("command").GetString());
    }

    [Fact]
    public void Copilot_project_uses_the_servers_root_key_and_a_pointer_block()
    {
        _installer.Install(
            AgentEnv.Copilot, InstallScope.Project, skills: true, mcp: true, workspaceArg: "api/tap", force: false);

        var server = Json(ProjectFile(".vscode/mcp.json")).GetProperty("servers").GetProperty("tap-studio");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Contains("api/tap", server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));

        Assert.True(File.Exists(ProjectFile(".tap/agent/tap-author/SKILL.md")));
        var instructions = File.ReadAllText(ProjectFile(".github/copilot-instructions.md"));
        Assert.Contains(".tap/agent/tap-studio/SKILL.md", instructions);
    }

    [Fact]
    public void The_pointer_block_is_replaced_not_duplicated_and_content_survives()
    {
        var agentsMd = ProjectFile("AGENTS.md");
        File.WriteAllText(agentsMd, "# My project\n\nHouse rules live here.\n");

        _installer.Install(AgentEnv.Codex, InstallScope.Project, skills: true, mcp: false, null, false);
        _installer.Install(AgentEnv.Codex, InstallScope.Project, skills: true, mcp: false, null, false);
        _installer.Install(AgentEnv.OpenCode, InstallScope.Project, skills: true, mcp: false, null, false);

        var text = File.ReadAllText(agentsMd);
        Assert.Contains("House rules live here.", text);
        Assert.Equal(1, CountOf(text, "<!-- tap-agent:start -->"));
        Assert.Equal(1, CountOf(text, "tap-author/SKILL.md"));
    }

    [Fact]
    public void OpenCode_project_registers_a_local_command_array()
    {
        _installer.Install(AgentEnv.OpenCode, InstallScope.Project, skills: false, mcp: true, ".", false);
        var entry = Json(ProjectFile("opencode.json")).GetProperty("mcp").GetProperty("tap-studio");
        Assert.Equal("local", entry.GetProperty("type").GetString());
        Assert.Equal(new[] { "tap-studio", "mcp", "--workspace", "." },
            entry.GetProperty("command").EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    [Fact]
    public void Codex_user_scope_appends_toml_once_and_never_twice()
    {
        var config = Path.Combine(_home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "model = \"o4\"\n");

        var first = _installer.Install(AgentEnv.Codex, InstallScope.User, skills: false, mcp: true, null, false);
        var second = _installer.Install(AgentEnv.Codex, InstallScope.User, skills: false, mcp: true, null, false);

        var text = File.ReadAllText(config);
        Assert.StartsWith("model = \"o4\"", text);
        Assert.Equal(1, CountOf(text, "[mcp_servers.tap-studio]"));
        Assert.Contains("args = [\"mcp\"]", text);
        Assert.Single(first.Actions);
        Assert.Single(second.Skipped);
    }

    [Fact]
    public void User_scope_claude_mcp_and_copilot_are_manual_steps_not_writes()
    {
        var claude = _installer.Install(AgentEnv.Claude, InstallScope.User, skills: false, mcp: true, null, false);
        Assert.Contains(claude.Manual, m => m.Contains("claude mcp add"));

        var copilot = _installer.Install(AgentEnv.Copilot, InstallScope.User, skills: true, mcp: true, null, false);
        Assert.Empty(copilot.Actions);
        Assert.Equal(2, copilot.Manual.Count);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
