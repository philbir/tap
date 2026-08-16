using Tap.Execution.Agent;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio vars</c> — print the variable cascade as a run would see it.
///
/// <para>The debugging command. "Why did this request hit the wrong host" is nearly always a
/// variable resolving from a scope the author didn't expect, and the answer is invisible from
/// the outside: it is spread across a manifest, a collection, a stage, an environment, and
/// whatever providers are configured. This prints the merged result and where each value came
/// from.</para>
///
/// <para>Values from providers are <em>not</em> fetched — listing them means round-tripping to
/// Key Vault or 1Password for something the user only wanted to look at, and printing secret
/// material to a terminal that might be recorded. Provider-backed names are shown as available,
/// masked.</para>
/// </summary>
public sealed class VarsCommand : Command<VarsCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--request <PATH>")]
        [Description("Resolve the cascade as it would apply to this request (adds its collection, stage, and own vars).")]
        public string? Request { get; init; }

        [CommandOption("--show-secrets")]
        [Description("Print values marked secret instead of masking them. Off by default — this output is easy to paste somewhere permanent.")]
        public bool ShowSecrets { get; init; }
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

        EnvFile? env = null;
        if (settings.Env is { Length: > 0 })
        {
            if (!TargetResolver.TryResolve(workspace, settings.Env!, [WorkspaceKind.Env], out var found, out var error))
            {
                console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
                return ExitCode.UsageError;
            }
            env = (EnvFile)found.File;
        }
        else if (workspace.Manifest?.DefaultEnv is { } defaultRef)
        {
            env = workspace.Resolve(defaultRef) as EnvFile;
        }

        RequestFile? request = null;
        if (settings.Request is { Length: > 0 })
        {
            if (!TargetResolver.TryResolve(workspace, settings.Request!, [WorkspaceKind.Request], out var found, out var error))
            {
                console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
                return ExitCode.UsageError;
            }
            request = (RequestFile)found.File;
        }

        var collection = request is not null ? CollectionLocator.ForRequest(workspace, request) : null;
        var stage = collection?.FindStage(settings.Stage) ?? collection?.FindStage(collection.DefaultStage);

        if (!VariableInputs.TryCollect(settings.VarFiles, settings.Vars, out var overrides, out var varError))
        {
            console.MarkupLine($"[red]{Markup.Escape(varError)}[/]");
            return ExitCode.UsageError;
        }

        // Same order the renderer merges in, so what's printed is what a run would use.
        var merged = new Dictionary<string, (string Value, string Scope, bool Secret)>(StringComparer.Ordinal);
        Merge(merged, workspace.Manifest?.Vars, "workspace");
        Merge(merged, collection?.Vars, "collection");
        Merge(merged, stage?.Vars, "stage");
        Merge(merged, env?.Vars, "env");
        Merge(merged, request?.Vars, "request");
        foreach (var (name, value) in overrides) merged[name] = (value, "--var", false);

        var scope = new List<string> { $"workspace {host.RootDirectory}" };
        if (env is not null) scope.Add($"env {env.Name ?? env.RelativePath}");
        if (collection is not null) scope.Add($"collection {collection.Name ?? collection.RelativePath}");
        if (stage is not null) scope.Add($"stage {stage.Name}");
        console.MarkupLine($"[dim]{Markup.Escape(string.Join(" · ", scope))}[/]");
        console.WriteLine();

        if (merged.Count == 0)
        {
            console.MarkupLine("[dim]No variables in scope.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.None).HideHeaders();
            table.AddColumn("name");
            table.AddColumn("value");
            table.AddColumn("scope");
            foreach (var (name, entry) in merged.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var display = entry.Secret && !settings.ShowSecrets ? "***" : entry.Value;
                table.AddRow(
                    Markup.Escape(name),
                    entry.Secret && !settings.ShowSecrets ? $"[yellow]{display}[/]" : Markup.Escape(display),
                    $"[dim]{entry.Scope}[/]");
            }
            console.Write(table);
        }

        // Providers are listed, not enumerated — see the class comment.
        var registry = host.CreateRegistry(env);
        var providers = registry.Providers.Select(p => p.Name).ToArray();
        if (providers.Length > 0)
        {
            console.WriteLine();
            console.MarkupLine($"[dim]providers: {Markup.Escape(string.Join(", ", providers))}[/]");
            console.MarkupLine("[dim]Provider values are resolved on demand and not listed here.[/]");
        }

        return ExitCode.Ok;
    }

    private static void Merge(
        Dictionary<string, (string Value, string Scope, bool Secret)> into,
        IReadOnlyDictionary<string, VarSpec>? vars,
        string scope)
    {
        foreach (var (name, spec) in vars ?? new Dictionary<string, VarSpec>())
        {
            if (spec.Default is not { } value) continue;
            into[name] = (value, scope, spec.Secret);
        }
    }
}
