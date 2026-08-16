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
        [Description("Workspace directory. Defaults to the nearest ancestor containing workspace.tap, or the first workspace found beneath the working directory.")]
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

        // Errors and warnings are reported together but only errors decide the exit code — a
        // deprecation notice must not turn every unmigrated workspace's CI red.
        var errors = workspace.Errors.Where(e => e.Severity == WorkspaceErrorSeverity.Error).ToArray();
        var warnings = workspace.Errors.Where(e => e.Severity == WorkspaceErrorSeverity.Warning).ToArray();

        foreach (var error in errors) Report(console, error, "[red]✘[/]");

        // Warnings that apply to every file in the workspace — the legacy-extension notice on an
        // unmigrated workspace is one per file — would otherwise bury the errors above them under
        // a hundred identical paragraphs. Show one, count the rest, unless asked for everything.
        foreach (var group in warnings.GroupBy(w => w.Code, StringComparer.Ordinal))
        {
            var shown = settings.Verbose ? group.ToArray() : group.Take(1).ToArray();
            foreach (var warning in shown) Report(console, warning, "[yellow]![/]");

            var hidden = group.Count() - shown.Length;
            if (hidden > 0)
                console.MarkupLine($"   [dim]… and {hidden} more {(hidden == 1 ? "file" : "files")} (-v to list)[/]");
        }

        var counts = Summarize(workspace.Files);
        console.WriteLine();

        var warningSuffix = warnings.Length == 0
            ? string.Empty
            : $" [yellow]{warnings.Length} {(warnings.Length == 1 ? "warning" : "warnings")}[/]";

        if (errors.Length == 0)
        {
            console.MarkupLine($"[green]OK[/]  {workspace.Files.Count} files loaded [dim]({counts})[/]{warningSuffix}");
            return ExitCode.Ok;
        }

        console.MarkupLine(
            $"[red]FAIL[/]  {errors.Length} "
            + $"{(errors.Length == 1 ? "problem" : "problems")} in {workspace.Files.Count} files [dim]({counts})[/]{warningSuffix}");
        return ExitCode.WorkspaceError;
    }

    private static void Report(IAnsiConsole console, WorkspaceError error, string marker)
    {
        console.MarkupLine(
            $"{marker} [bold]{Markup.Escape(error.Code)}[/] {Markup.Escape(error.RelativePath ?? "(workspace)")}"
            + (error.Line is { } line ? $":{line}" : string.Empty));
        console.MarkupLine($"   {Markup.Escape(error.Message)}");
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
