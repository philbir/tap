using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace;
using Tap.Workspace.Migration;
using Tap.Workspace.Model;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio migrate</c> — one-shot conversion of a workspace to the 0.7.0 <c>.tap</c>
/// file extensions.
///
/// <para>Renames every legacy <c>.md</c> workspace file <em>and</em> rewrites every reference
/// that pointed at one, because refs are literal relative path strings carrying an extension.
/// Doing only the rename leaves a workspace full of dangling refs, which is why this exists as
/// a command rather than as advice to run <c>git mv</c>.</para>
///
/// <para>Plans in full before touching disk, and refuses outright on a workspace that doesn't
/// parse — migrating broken refs compounds the damage instead of fixing it. Idempotent: a
/// migrated workspace is a no-op.</para>
/// </summary>
public sealed class MigrateCommand : Command<MigrateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace <DIR>")]
        [Description("Workspace directory. Defaults to the nearest ancestor containing workspace.tap (or a legacy tap.md), or the first workspace found beneath the working directory.")]
        public string? WorkspaceDirectory { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print what would change and exit without writing anything.")]
        public bool DryRun { get; init; }

        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor);

        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            console.MarkupLine($"[red]{Markup.Escape(locateError)}[/]");
            return ExitCode.WorkspaceError;
        }

        var workspace = new WorkspaceLoader().Load(root);
        var plan = WorkspaceMigrator.Plan(workspace, rel => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));

        if (plan.Blockers.Count > 0)
        {
            console.MarkupLine("[red]Refusing to migrate — fix these first:[/]");
            foreach (var blocker in plan.Blockers)
                console.MarkupLine($"  [red]✘[/] {Markup.Escape(blocker)}");
            console.WriteLine();
            console.MarkupLine("[dim]Migrating a workspace that doesn't load would bake the breakage into the renamed files.[/]");
            return ExitCode.WorkspaceError;
        }

        if (plan.IsNoOp)
        {
            console.MarkupLine($"[green]Nothing to do[/] — {Markup.Escape(root)} already uses the .tap extensions.");
            return ExitCode.Ok;
        }

        foreach (var change in plan.Changes)
        {
            if (change.IsRename)
                console.MarkupLine($"[yellow]rename[/] {Markup.Escape(change.FromRelativePath)} [dim]→[/] {Markup.Escape(Path.GetFileName(change.ToRelativePath))}");
            else
                console.MarkupLine($"[cyan]edit[/]   {Markup.Escape(change.FromRelativePath)}");

            foreach (var r in change.Refs)
                console.MarkupLine($"         [dim]:{r.Line} {Markup.Escape(r.Key)}: {Markup.Escape(r.From)} → {Markup.Escape(r.To)}[/]");
        }

        console.WriteLine();
        var summary = $"{plan.RenameCount} {(plan.RenameCount == 1 ? "file" : "files")} renamed, "
            + $"{plan.RefRewriteCount} {(plan.RefRewriteCount == 1 ? "ref" : "refs")} rewritten";

        if (settings.DryRun)
        {
            console.MarkupLine($"[bold]Dry run[/] — {summary}. Nothing was written.");
            return ExitCode.Ok;
        }

        try
        {
            Execute(root, plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[red]Migration failed partway through: {Markup.Escape(ex.Message)}[/]");
            console.MarkupLine("[dim]The workspace may be half-migrated. Restore it with 'git checkout -- .' (or your backup) and retry.[/]");
            return ExitCode.WorkspaceError;
        }

        // Re-load from disk rather than trusting the plan: the whole point of the command is that
        // the workspace still resolves afterwards, and only a real load proves it.
        var after = new WorkspaceLoader().Load(root);
        var remaining = after.Errors.Where(e => e.Severity == WorkspaceErrorSeverity.Error).ToArray();
        if (remaining.Length > 0)
        {
            console.MarkupLine($"[red]Migrated, but the workspace now has {remaining.Length} error(s):[/]");
            foreach (var error in remaining)
                console.MarkupLine($"  [red]✘[/] {Markup.Escape(error.Code)} {Markup.Escape(error.RelativePath ?? "(workspace)")}: {Markup.Escape(error.Message)}");
            console.WriteLine();
            console.MarkupLine("[dim]Restore with 'git checkout -- .' (or your backup) and report this — a clean workspace should migrate cleanly.[/]");
            return ExitCode.WorkspaceError;
        }

        console.MarkupLine($"[green]Migrated[/] — {summary}.");
        console.MarkupLine($"[dim]{after.Files.Count} {(after.Files.Count == 1 ? "file loads" : "files load")} with no errors. 'git add -A' will pick the renames up as moves.[/]");
        return ExitCode.Ok;
    }

    /// <summary>
    /// Applies the plan: content first at the old path, then the rename. Doing it in that order
    /// means each file is only ever touched by one operation that can fail, and a file is never
    /// left holding the old refs under its new name.
    /// </summary>
    private static void Execute(string root, MigrationPlan plan)
    {
        string Absolute(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        foreach (var change in plan.Changes)
        {
            if (change.RewrittenContent is { } content)
                File.WriteAllText(Absolute(change.FromRelativePath), content);
        }

        foreach (var change in plan.Changes.Where(c => c.IsRename))
            File.Move(Absolute(change.FromRelativePath), Absolute(change.ToRelativePath));
    }
}
