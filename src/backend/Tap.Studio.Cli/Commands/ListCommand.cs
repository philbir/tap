using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Execution.Agent;
using Tap.Execution.Contracts;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio list [kind]</c> — what's in the workspace, for humans and for agents.
///
/// <para>Discovery keeps working on a partially-broken workspace: files that parsed are
/// listed, parse errors go to stderr as warnings. A listing that refuses to say anything
/// because one file is bad would hide exactly the information needed to fix it.</para>
/// </summary>
public sealed class ListCommand : Command<ListCommand.Settings>
{
    private static readonly string[] Kinds = ["requests", "collections", "envs", "tests", "auths"];

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[KIND]")]
        [Description("Narrow to one kind: requests, collections, envs, tests, or auths. Omit for everything.")]
        public string? Kind { get; init; }

        [CommandOption("-w|--workspace <DIR>")]
        [Description("Workspace directory. Defaults to the nearest ancestor containing workspace.tap, or the first workspace found beneath the working directory.")]
        public string? WorkspaceDirectory { get; init; }

        [CommandOption("--json")]
        [Description("Print the inventory as JSON on stdout. Warnings go to stderr.")]
        public bool Json { get; init; }

        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor, toStdErr: settings.Json);

        var kind = settings.Kind?.Trim().ToLowerInvariant();
        if (kind is { Length: > 0 } && !Kinds.Contains(kind))
        {
            console.MarkupLine($"[red]'{Markup.Escape(settings.Kind!)}' is not a listable kind. Use one of: {string.Join(", ", Kinds)}.[/]");
            return ExitCode.UsageError;
        }

        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            console.MarkupLine($"[red]{Markup.Escape(locateError)}[/]");
            return ExitCode.WorkspaceError;
        }

        var host = CliWorkspaceHost.Load(root);
        foreach (var error in host.Workspace.Errors.Take(10))
        {
            console.MarkupLine($"[yellow]warning:[/] {Markup.Escape($"{error.Code} {error.RelativePath ?? "(workspace)"}: {error.Message}")}");
        }

        var inventory = WorkspaceInventory.Build(host.Workspace);

        if (settings.Json)
        {
            switch (kind)
            {
                case "requests": JsonOutput.Write(inventory.Requests); break;
                case "collections": JsonOutput.Write(inventory.Collections); break;
                case "envs": JsonOutput.Write(inventory.Envs); break;
                case "tests": JsonOutput.Write(inventory.Tests); break;
                case "auths": JsonOutput.Write(inventory.Auths); break;
                default: JsonOutput.Write(inventory); break;
            }
            return ExitCode.Ok;
        }

        var first = true;
        if (kind is null or "collections") PrintCollections(console, inventory, ref first);
        if (kind is null or "requests") PrintRequests(console, inventory, ref first);
        if (kind is null or "envs") PrintEnvs(console, inventory, ref first);
        if (kind is null or "tests") PrintTests(console, inventory, ref first);
        if (kind is null or "auths") PrintAuths(console, inventory, ref first);
        return ExitCode.Ok;
    }

    private static void Section(IAnsiConsole console, string title, ref bool first)
    {
        if (!first) console.WriteLine();
        first = false;
        console.MarkupLine($"[bold]{title}[/]");
    }

    private static void PrintCollections(IAnsiConsole console, WorkspaceInventoryDto inventory, ref bool first)
    {
        if (inventory.Collections.Count == 0) return;
        Section(console, "Collections", ref first);
        foreach (var c in inventory.Collections)
        {
            var envs = c.Environments.Count > 0
                ? $" · envs: {string.Join(", ", c.Environments.Select(System.IO.Path.GetFileName))}"
                : string.Empty;
            console.MarkupLine(
                $"  {Markup.Escape(c.Name)}  [dim]{c.RequestCount} requests · {Markup.Escape(c.BaseUrl ?? "(no baseUrl)")}{Markup.Escape(envs)} · {Markup.Escape(c.Path)}[/]");
        }
    }

    private static void PrintRequests(IAnsiConsole console, WorkspaceInventoryDto inventory, ref bool first)
    {
        if (inventory.Requests.Count == 0) return;
        Section(console, "Requests", ref first);
        foreach (var r in inventory.Requests)
        {
            var auth = r.Auth is null ? string.Empty : $" · auth: {r.Auth}";
            console.MarkupLine(
                $"  {Markup.Escape(r.Method),-7} {Markup.Escape(r.UrlTemplate)}  [dim]{Markup.Escape(r.Name)}{Markup.Escape(auth)} · {Markup.Escape(r.Path)}[/]");
        }
    }

    private static void PrintEnvs(IAnsiConsole console, WorkspaceInventoryDto inventory, ref bool first)
    {
        if (inventory.Envs.Count == 0) return;
        Section(console, "Environments", ref first);
        foreach (var e in inventory.Envs)
        {
            var mark = e.IsDefault ? " [green](default)[/]" : string.Empty;
            console.MarkupLine($"  {Markup.Escape(e.Name)}{mark}  [dim]{Markup.Escape(e.Path)}[/]");
        }
    }

    private static void PrintTests(IAnsiConsole console, WorkspaceInventoryDto inventory, ref bool first)
    {
        if (inventory.Tests.Count == 0) return;
        Section(console, "Tests", ref first);
        foreach (var t in inventory.Tests)
        {
            var unit = t.Kind == "flow" ? "steps" : "tests";
            console.MarkupLine($"  {Markup.Escape(t.Name)}  [dim]{t.Kind} · {t.EntryCount} {unit} · {Markup.Escape(t.Path)}[/]");
        }
    }

    private static void PrintAuths(IAnsiConsole console, WorkspaceInventoryDto inventory, ref bool first)
    {
        if (inventory.Auths.Count == 0) return;
        Section(console, "Auth profiles", ref first);
        foreach (var a in inventory.Auths)
        {
            console.MarkupLine($"  {Markup.Escape(a.Name)}  [dim]{Markup.Escape(a.Type)} · {Markup.Escape(a.Path)}[/]");
        }
    }
}
