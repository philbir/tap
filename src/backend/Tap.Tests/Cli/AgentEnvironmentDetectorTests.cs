using Tap.Studio.Cli.Agent;

namespace Tap.Tests.Cli;

/// <summary>
/// Detection signals for the <c>agent init</c> wizard, against fabricated homes, projects,
/// and PATH directories. The copilot probe also consults the real machine
/// (/Applications on macOS), so the negative case deliberately doesn't assert on it.
/// </summary>
public sealed class AgentEnvironmentDetectorTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("tap-detect-proj-").FullName;
    private readonly string _home = Directory.CreateTempSubdirectory("tap-detect-home-").FullName;
    private readonly string _bin = Directory.CreateTempSubdirectory("tap-detect-bin-").FullName;

    public void Dispose()
    {
        Directory.Delete(_project, recursive: true);
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_bin, recursive: true);
    }

    private IReadOnlyDictionary<AgentEnv, string> Detect()
        => AgentEnvironmentDetector.Detect(_project, _home, [_bin]);

    [Fact]
    public void A_cli_on_path_is_evidence()
    {
        File.WriteAllText(Path.Combine(_bin, "codex"), "#!/bin/sh\n");
        var found = Detect();
        Assert.Equal("codex CLI on PATH", found[AgentEnv.Codex]);
    }

    [Fact]
    public void Config_directories_are_evidence()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "opencode"));
        Directory.CreateDirectory(Path.Combine(_project, ".vscode"));

        var found = Detect();
        Assert.Equal("~/.claude exists", found[AgentEnv.Claude]);
        Assert.Equal("~/.config/opencode exists", found[AgentEnv.OpenCode]);
        Assert.Equal(".vscode/ in project", found[AgentEnv.Copilot]);
    }

    [Fact]
    public void A_project_mcp_json_suggests_claude()
    {
        File.WriteAllText(Path.Combine(_project, ".mcp.json"), "{}");
        Assert.Equal(".mcp.json in project", Detect()[AgentEnv.Claude]);
    }

    [Fact]
    public void Nothing_planted_detects_nothing_for_the_hermetic_probes()
    {
        var found = Detect();
        Assert.DoesNotContain(AgentEnv.Claude, found.Keys);
        Assert.DoesNotContain(AgentEnv.Codex, found.Keys);
        Assert.DoesNotContain(AgentEnv.OpenCode, found.Keys);
        // Copilot intentionally unasserted: its probe may see the machine's real VS Code.
    }
}
