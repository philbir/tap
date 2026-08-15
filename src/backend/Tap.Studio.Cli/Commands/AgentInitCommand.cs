using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Studio.Cli.Agent;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio agent init --env claude</c> — set a repo (or a machine) up for AI-agent
/// use of a Tap workspace: install the skills that teach an agent to operate and author
/// workspaces, and register the MCP server in the environment's own config format.
///
/// <para>With no <c>--env</c> on an interactive terminal it becomes a wizard: detect which
/// agent environments this machine plausibly runs, preselect those, and ask three
/// questions. Headless callers (CI, an agent shelling out) keep the crisp usage error —
/// a prompt nobody can see is a hang, not a wizard.</para>
/// </summary>
public sealed class AgentInitCommand : Command<AgentInitCommand.Settings>
{
    private static readonly Dictionary<string, AgentEnv> Envs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = AgentEnv.Claude,
        ["codex"] = AgentEnv.Codex,
        ["copilot"] = AgentEnv.Copilot,
        ["opencode"] = AgentEnv.OpenCode,
    };

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--env <ENV>")]
        [Description("Agent environment to set up: claude, codex, copilot, or opencode. Repeatable.")]
        public string[]? Environments { get; init; }

        [CommandOption("--scope <SCOPE>")]
        [Description("Where to install: 'project' (checked-in files, the default) or 'user' (this machine, all projects).")]
        public string Scope { get; init; } = "project";

        [CommandOption("--skills")]
        [Description("Install only the skills (the agent guides).")]
        public bool SkillsOnly { get; init; }

        [CommandOption("--mcp")]
        [Description("Install only the MCP server registration.")]
        public bool McpOnly { get; init; }

        [CommandOption("--project-dir <DIR>")]
        [Description("Project root for project-scope files. Defaults to the current directory.")]
        public string ProjectDir { get; init; } = ".";

        [CommandOption("-w|--workspace <DIR>")]
        [Description("Workspace the project-scope MCP registration points at. Defaults to the nearest ancestor containing tap.md, or the first workspace found beneath the working directory.")]
        public string? WorkspaceDirectory { get; init; }

        [CommandOption("--force")]
        [Description("Replace an existing 'tap-studio' MCP registration instead of leaving it as-is.")]
        public bool Force { get; init; }

        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor);

        var projectDir = Path.GetFullPath(settings.ProjectDir);
        if (!Directory.Exists(projectDir))
        {
            console.MarkupLine($"[red]Project directory '{Markup.Escape(settings.ProjectDir)}' does not exist.[/]");
            return ExitCode.UsageError;
        }

        List<AgentEnv> envs;
        InstallScope scope;
        bool skills, mcp;

        var requested = settings.Environments ?? [];
        if (requested.Length == 0)
        {
            if (!CanPrompt())
            {
                console.MarkupLine($"[red]Pass at least one --env. Available: {string.Join(", ", Envs.Keys)}. (Run without arguments on an interactive terminal for the setup wizard.)[/]");
                return ExitCode.UsageError;
            }
            if (!RunWizard(projectDir, out envs, out scope, out skills, out mcp)) return ExitCode.Ok;
        }
        else
        {
            envs = [];
            foreach (var raw in requested)
            {
                if (!Envs.TryGetValue(raw.Trim(), out var env))
                {
                    console.MarkupLine($"[red]'{Markup.Escape(raw)}' is not a known agent environment. Available: {string.Join(", ", Envs.Keys)}.[/]");
                    return ExitCode.UsageError;
                }
                if (!envs.Contains(env)) envs.Add(env);
            }

            if (!TryParseScope(settings.Scope, out scope))
            {
                console.MarkupLine("[red]--scope must be 'project' or 'user'.[/]");
                return ExitCode.UsageError;
            }

            // Neither flag means both — init exists to do the whole job in one command.
            skills = settings.SkillsOnly || !settings.McpOnly;
            mcp = settings.McpOnly || !settings.SkillsOnly;
        }

        // The workspace arg baked into a project-scope registration must be a relative,
        // checked-in-safe path. When no workspace exists yet, register without one — the
        // server resolves the workspace by walking up from wherever the agent runs it.
        string? workspaceArg = null;
        if (mcp && scope == InstallScope.Project)
        {
            if (WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, projectDir, out var root, out _))
            {
                var relative = Path.GetRelativePath(projectDir, root).Replace(Path.DirectorySeparatorChar, '/');
                workspaceArg = relative == "." ? "." : relative;
            }
            else
            {
                console.MarkupLine("[yellow]No tap.md found — registering the MCP server without a pinned --workspace; it will resolve the workspace from the agent's working directory.[/]");
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installer = new AgentInstaller(projectDir, home);

        foreach (var env in envs)
        {
            console.MarkupLine($"[bold]{env.ToString().ToLowerInvariant()}[/] [dim]({scope.ToString().ToLowerInvariant()})[/]");
            var report = installer.Install(env, scope, skills, mcp, workspaceArg, settings.Force);
            foreach (var action in report.Actions) console.MarkupLine($"  [green]✔[/] {Markup.Escape(action)}");
            foreach (var skip in report.Skipped) console.MarkupLine($"  [dim]○ {Markup.Escape(skip)}[/]");
            foreach (var manual in report.Manual) console.MarkupLine($"  [yellow]→[/] {Markup.Escape(manual)}");
        }

        console.MarkupLine("[dim]Restart the agent session so it picks up new skills and tools.[/]");
        return ExitCode.Ok;
    }

    /// <summary>Prompts only make sense when a human is on both ends of the pipe — and
    /// Spectre's selection prompts additionally need ANSI, so a dumb terminal gets the
    /// usage error rather than an exception.</summary>
    private static bool CanPrompt()
        => !Console.IsInputRedirected
        && !Console.IsOutputRedirected
        && AnsiConsole.Console.Profile.Capabilities.Interactive
        && AnsiConsole.Console.Profile.Capabilities.Ansi;

    /// <summary>Three questions, detection-ranked. Returns false when the user selected
    /// nothing to do (which the multiselect's Required() makes impossible today, but a
    /// future escape key shouldn't turn into a full install).</summary>
    private static bool RunWizard(
        string projectDir, out List<AgentEnv> envs, out InstallScope scope, out bool skills, out bool mcp)
    {
        var console = AnsiConsole.Console;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var detected = AgentEnvironmentDetector.Detect(projectDir, home);

        var envPrompt = new MultiSelectionPrompt<AgentEnv>()
            .Title("Which agent environments should be set up? [dim](space toggles, enter confirms)[/]")
            .Required()
            .UseConverter(env => detected.TryGetValue(env, out var evidence)
                ? $"{env.ToString().ToLowerInvariant()} [dim]({evidence})[/]"
                : env.ToString().ToLowerInvariant());
        foreach (var env in Envs.Values.Distinct())
        {
            var choice = envPrompt.AddChoice(env);
            if (detected.ContainsKey(env)) choice.Select();
        }
        envs = console.Prompt(envPrompt);

        scope = console.Prompt(
            new SelectionPrompt<InstallScope>()
                .Title("Install where?")
                .UseConverter(s => s == InstallScope.Project
                    ? "project — checked-in files, shared with the team"
                    : "user — this machine only, works in every project")
                .AddChoices(InstallScope.Project, InstallScope.User));

        var what = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Install what?")
                .AddChoices("Skills + MCP server (recommended)", "Skills only", "MCP server only"));
        skills = what is not "MCP server only";
        mcp = what is not "Skills only";

        return envs.Count > 0;
    }

    private static bool TryParseScope(string raw, out InstallScope scope)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "project": scope = InstallScope.Project; return true;
            case "user": scope = InstallScope.User; return true;
            default: scope = default; return false;
        }
    }
}
