using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio lint</c> — load every file in the workspace and report what doesn't parse.
///
/// <para>Cheap enough to run on every pull request, and it catches the class of mistake that is
/// otherwise only discovered when someone opens the Studio: a malformed assertion, a step with
/// no request, a <c>kind:</c> that disagrees with its filename. It sends nothing, so it needs
/// no credentials and no network.</para>
/// </summary>
public sealed class LintCommand : Command<LintCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace <DIR>")]
        [Description("Workspace directory. Defaults to the nearest ancestor containing tap.md.")]
        public string? WorkspaceDirectory { get; init; }

        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }

        [CommandOption("-v|--verbose")]
        [Description("List every file that loaded, not just the ones that didn't.")]
        public bool Verbose { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor);

        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            console.MarkupLine($"[red]{Markup.Escape(locateError)}[/]");
            return ExitCode.WorkspaceError;
        }

        var host = CliWorkspaceHost.Load(root);
        var workspace = host.Workspace;

        if (settings.Verbose)
        {
            foreach (var file in workspace.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
                console.MarkupLine($"[green]✔[/] [dim]{file.Kind.ToString().ToLowerInvariant()}[/] {Markup.Escape(file.RelativePath)}");
            console.WriteLine();
        }

        foreach (var error in workspace.Errors)
        {
            console.MarkupLine(
                $"[red]✘[/] [bold]{Markup.Escape(error.Code)}[/] {Markup.Escape(error.RelativePath ?? "(workspace)")}"
                + (error.Line is { } line ? $":{line}" : string.Empty));
            console.MarkupLine($"   {Markup.Escape(error.Message)}");
        }

        var counts = Summarize(workspace.Files);
        console.WriteLine();

        if (workspace.Errors.Count == 0)
        {
            console.MarkupLine($"[green]OK[/]  {workspace.Files.Count} files loaded [dim]({counts})[/]");
            return ExitCode.Ok;
        }

        console.MarkupLine(
            $"[red]FAIL[/]  {workspace.Errors.Count} "
            + $"{(workspace.Errors.Count == 1 ? "problem" : "problems")} in {workspace.Files.Count} files [dim]({counts})[/]");
        return ExitCode.WorkspaceError;
    }

    private static string Summarize(IReadOnlyList<WorkspaceFile> files)
    {
        var counts = files
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}{(g.Count() == 1 ? "" : "s")}");
        return string.Join(", ", counts);
    }
}
