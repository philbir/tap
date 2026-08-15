using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Execution.Agent;
using Tap.Execution.Contracts;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio describe &lt;name|path&gt;</c> — one request's full template surface: what it
/// will send, which variables it reads, which auth profile it rides on (name and type only),
/// and what it asserts. Everything here is file content — nothing is rendered, so nothing
/// resolved (and nothing secret) can appear.
/// </summary>
public sealed class DescribeCommand : Command<DescribeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Request: a workspace-relative path, the name: from its frontmatter, or its filename stem.")]
        public string Target { get; init; } = string.Empty;

        [CommandOption("-w|--workspace <DIR>")]
        [Description("Workspace directory. Defaults to the nearest ancestor containing tap.md, or the first workspace found beneath the working directory.")]
        public string? WorkspaceDirectory { get; init; }

        [CommandOption("--json")]
        [Description("Print the description as JSON on stdout.")]
        public bool Json { get; init; }

        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor, toStdErr: settings.Json);

        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            console.MarkupLine($"[red]{Markup.Escape(locateError)}[/]");
            return ExitCode.WorkspaceError;
        }

        var host = CliWorkspaceHost.Load(root);
        if (!TargetResolver.TryResolve(
                host.Workspace, settings.Target, [WorkspaceKind.Request], out var target, out var resolveError))
        {
            console.MarkupLine($"[red]{Markup.Escape(resolveError)}[/]");
            return ExitCode.UsageError;
        }

        var request = (RequestFile)host.Workspace.FindByPath(target.Path)!;
        var described = WorkspaceInventory.Describe(host.Workspace, request);

        if (settings.Json)
        {
            JsonOutput.Write(described);
            return ExitCode.Ok;
        }

        Print(console, described);
        return ExitCode.Ok;
    }

    private static void Print(IAnsiConsole console, RequestDescriptionDto d)
    {
        console.MarkupLine($"[bold]{Markup.Escape(d.Name)}[/]  [dim]{Markup.Escape(d.Path)}[/]");
        console.MarkupLine($"  {Markup.Escape(d.Method)} {Markup.Escape(d.UrlTemplate)}  [dim]({Markup.Escape(d.Protocol)})[/]");

        if (d.Collection is not null)
        {
            var stages = d.Stages.Count > 0 ? $" · stages: {string.Join(", ", d.Stages)}" : string.Empty;
            console.MarkupLine($"  [dim]collection:[/] {Markup.Escape(d.Collection)}{Markup.Escape(stages)}");
        }
        console.MarkupLine(d.Auth is null
            ? "  [dim]auth:[/] none"
            : $"  [dim]auth:[/] {Markup.Escape(d.Auth)} [dim]({Markup.Escape(d.AuthType ?? "?")})[/]");

        foreach (var header in d.Headers)
        {
            console.MarkupLine($"  [dim]header:[/] {Markup.Escape(header.Name)}: {Markup.Escape(header.Value)}");
        }
        if (d.BodyTemplate is { Length: > 0 } body)
        {
            var preview = body.Length > 400 ? body[..400] + " […]" : body;
            console.MarkupLine($"  [dim]body:[/] {Markup.Escape(preview)}");
        }

        if (d.VariablesReferenced.Count > 0)
            console.MarkupLine($"  [dim]variables:[/] {Markup.Escape(string.Join(", ", d.VariablesReferenced))}");
        if (d.DeclaredVars.Count > 0)
            console.MarkupLine($"  [dim]declared vars:[/] {Markup.Escape(string.Join(", ", d.DeclaredVars))}");

        foreach (var assertion in d.Assertions)
        {
            console.MarkupLine($"  [dim]assert:[/] {Markup.Escape(assertion)}");
        }
        if (d.Tags.Count > 0)
            console.MarkupLine($"  [dim]tags:[/] {Markup.Escape(string.Join(", ", d.Tags))}");
    }
}
