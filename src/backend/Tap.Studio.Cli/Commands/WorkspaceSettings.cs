using System.ComponentModel;
using Spectre.Console.Cli;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// Options every workspace-scoped command shares: which workspace, which environment, which
/// stage, and what the run's input variables are.
/// </summary>
public class WorkspaceSettings : CommandSettings
{
    [CommandOption("-w|--workspace <DIR>")]
    [Description("Workspace directory. Defaults to the nearest ancestor containing workspace.tap, or the first workspace found beneath the working directory.")]
    public string? WorkspaceDirectory { get; init; }

    [CommandOption("-e|--env <NAME|PATH>")]
    [Description("Environment to resolve variables against. Defaults to the manifest's defaultEnv.")]
    public string? Env { get; init; }

    [CommandOption("-s|--stage <NAME>")]
    [Description("Collection stage to resolve baseUrl and variables against.")]
    public string? Stage { get; init; }

    [CommandOption("--var <NAME=VALUE>")]
    [Description("Set an input variable for this run. Repeatable. Overrides every file scope.")]
    public string[]? Vars { get; init; }

    [CommandOption("--var-file <FILE>")]
    [Description(".env or JSON file of input variables. Repeatable; later files win, and --var beats all of them.")]
    public string[]? VarFiles { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Show request lines, bound values, and passing assertions — not just failures.")]
    public bool Verbose { get; init; }

    [CommandOption("--no-color")]
    [Description("Disable colour. NO_COLOR and a redirected stdout are honoured automatically.")]
    public bool NoColor { get; init; }
}
