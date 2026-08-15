using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Execution;
using Tap.Execution.Contracts;
using Tap.Execution.Testing;
using Tap.Execution.Workspace;
using Tap.Studio.Cli.Auth;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio test &lt;name|path&gt;</c> — run a test set or a flow and report the verdict.
///
/// <para>The command a CI job calls. Everything about it is shaped by that: it exits with a
/// code a pipeline can branch on, it streams results as they happen so a stuck run is visible,
/// and it never waits for input.</para>
/// </summary>
public sealed class TestCommand : AsyncCommand<TestCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Test set or flow: a workspace-relative path, the name: from its frontmatter, or its filename stem.")]
        public string? Target { get; init; }

        [CommandOption("--tag <TAG>")]
        [Description("Run every test set and flow carrying this tag instead of naming one. Repeatable; a file matching any of them is included.")]
        public string[]? Tags { get; init; }

        [CommandOption("--filter <TEXT>")]
        [Description("Narrow to the tests whose name contains this. Applies within a test set.")]
        public string? Filter { get; init; }

        [CommandOption("--only <INDEX>")]
        [Description("Run just one entry of a test set, by its zero-based index.")]
        public int? Only { get; init; }

        [CommandOption("--fail-fast")]
        [Description("Stop at the first failing test, whatever the set's onFailure says.")]
        public bool FailFast { get; init; }

        [CommandOption("--use-cached-tokens")]
        [Description("Allow ~/.tap/auth-tokens.json to satisfy auth. Off by default: a CI run should not pass on a token a developer minted by hand.")]
        public bool UseCachedTokens { get; init; }

        [CommandOption("--output <FORMAT>")]
        [Description("Write a report: junit, trx, json, or markdown.")]
        public string? Output { get; init; }

        [CommandOption("--output-file <PATH>")]
        [Description("Where to write the report. Defaults to test-results.<ext> beside the working directory.")]
        public string? OutputFile { get; init; }

        [CommandOption("--list")]
        [Description("List the test sets and flows in the workspace and exit.")]
        public bool List { get; init; }

        [CommandOption("--json")]
        [Description("Print the run result as JSON on stdout, secrets redacted. Progress and errors go to stderr.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor, toStdErr: settings.Json);
        var reporter = new ConsoleReporter(console, settings.Verbose);

        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            reporter.Error(locateError);
            return ExitCode.WorkspaceError;
        }

        using var http = new HttpClient();
        var host = CliWorkspaceHost.Load(root);

        // A workspace that doesn't parse is not a failed test — it's a broken repo, and the
        // difference matters to whoever reads the pipeline.
        if (host.Workspace.Errors.Count > 0)
        {
            foreach (var error in host.Workspace.Errors.Take(10))
                reporter.Error($"{error.Code} {error.RelativePath ?? "(workspace)"}: {error.Message}");
            if (host.Workspace.Errors.Count > 10)
                console.MarkupLine($"[dim]  +{host.Workspace.Errors.Count - 10} more[/]");
            return ExitCode.WorkspaceError;
        }

        if (settings.List)
        {
            if (settings.Json)
            {
                JsonOutput.Write(Tap.Execution.Agent.WorkspaceInventory.Build(host.Workspace).Tests);
                return ExitCode.Ok;
            }
            return ListTargets(console, host);
        }

        var tags = settings.Tags ?? [];
        var named = !string.IsNullOrWhiteSpace(settings.Target);

        if (named && tags.Length > 0)
        {
            // Defining a precedence between them would just be a rule to memorise.
            reporter.Error($"Name a target or pass --tag, not both. Drop one of '{settings.Target}' or --tag.");
            return ExitCode.UsageError;
        }

        IReadOnlyList<ResolvedTarget> targets;
        if (tags.Length > 0)
        {
            if (!TargetSelector.TryByTags(host.Workspace, tags, out targets, out var tagError))
            {
                reporter.Error(tagError);
                return ExitCode.UsageError;
            }
        }
        else if (named)
        {
            if (!TargetResolver.TryResolve(
                    host.Workspace, settings.Target!,
                    [WorkspaceKind.Test, WorkspaceKind.Flow],
                    out var single, out var resolveError))
            {
                reporter.Error(resolveError);
                return ExitCode.UsageError;
            }
            targets = [single];
        }
        else
        {
            reporter.Error("Name a test set or flow to run, pass --tag to select by tag, or --list to see what's available.");
            return ExitCode.UsageError;
        }

        if (!VariableInputs.TryCollect(settings.VarFiles, settings.Vars, out var variables, out var varError))
        {
            reporter.Error(varError);
            return ExitCode.UsageError;
        }

        host.WithTokens(h => HeadlessAuthTokenSource.Create(
            h.Workspace, h.CreateRegistry, http, h.RootDirectory, settings.UseCachedTokens));

        var envPath = ResolveEnvArgument(host, settings.Env);
        if (settings.Env is { Length: > 0 } && envPath is null)
        {
            reporter.Error($"No environment matches '{settings.Env}'. "
                + $"Available: {string.Join(", ", host.Workspace.Environments.Select(e => e.Name ?? TargetResolver.Stem(e.RelativePath)))}");
            return ExitCode.UsageError;
        }

        ExecutionIdentity.UserAgent = $"tap-studio-cli/{ExecutionIdentity.Version}";

        // One redactor accumulated across every step of every target, so the serialized JSON
        // report can be scrubbed in a single pass at the end.
        var redactor = Tap.Workspace.Rendering.SecretRedactor.None;
        var runner = new TestRunner(new RequestPipeline(host))
        {
            OnRendered = rendered => redactor = redactor.Merge(rendered.Redactor),
        };
        var results = new List<TestRunResultDto>(targets.Count);

        foreach (var target in targets)
        {
            var request = new RunTestRequestDto(
                Path: target.Path,
                Env: envPath,
                Stage: settings.Stage,
                Only: settings.Only,
                Overrides: variables.Count > 0 ? variables : null,
                FailFast: settings.FailFast,
                Filter: settings.Filter);

            try
            {
                results.Add(await runner.RunAsync(request, new TestRunner.Callbacks(
                    OnStart: (start, _) => { reporter.Started(start); return ValueTask.CompletedTask; },
                    OnStep: (step, _) => { reporter.Step(step); return ValueTask.CompletedTask; },
                    OnEntry: (entry, _) => { reporter.Entry(entry); return ValueTask.CompletedTask; }),
                    ct));
            }
            catch (OperationCanceledException)
            {
                reporter.Error("Cancelled.");
                return ExitCode.Cancelled;
            }
            catch (HeadlessAuthUnavailableException ex)
            {
                reporter.Error(ex.Message);
                return ExitCode.AuthUnavailable;
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                // --filter / --only asking for something that isn't there.
                reporter.Error(FirstLine(ex.Message));
                return ExitCode.UsageError;
            }
            catch (WorkspaceParseException ex)
            {
                reporter.Error($"{ex.Error.Code} {ex.Error.RelativePath ?? target.Path}: {ex.Error.Message}");
                return ExitCode.WorkspaceError;
            }
            catch (FileNotFoundException ex)
            {
                reporter.Error(ex.Message);
                return ExitCode.WorkspaceError;
            }
        }

        reporter.Finished(results);

        if (settings.Json)
        {
            Console.Out.WriteLine(redactor.Redact(ReportWriter.Json(results)));
        }

        if (settings.Output is { Length: > 0 })
        {
            if (!ReportWriter.TryWrite(settings.Output, settings.OutputFile, results, out var written, out var reportError))
            {
                reporter.Error(reportError);
                return ExitCode.UsageError;
            }
            console.MarkupLine($"[dim]report written to {Markup.Escape(written)}[/]");
        }

        return results.All(r => r.Ok) ? ExitCode.Ok : ExitCode.TestFailed;
    }

    /// <summary>ArgumentException tacks "(Parameter 'x')" onto its message — an implementation
    /// detail of the engine's API, not something a CLI user typed.</summary>
    private static string FirstLine(string message)
    {
        var cut = message.IndexOf(" (Parameter", StringComparison.Ordinal);
        return cut < 0 ? message : message[..cut];
    }

    /// <summary>Accepts an env by path, by <c>name:</c>, or by filename stem — the same three
    /// ways the run target is named, because being inconsistent about that is its own papercut.</summary>
    private static string? ResolveEnvArgument(IWorkspaceHost host, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        return TargetResolver.TryResolve(host.Workspace, query!, [WorkspaceKind.Env], out var env, out _)
            ? env.Path
            : null;
    }

    private static int ListTargets(IAnsiConsole console, IWorkspaceHost host)
    {
        var sets = host.Workspace.TestSets;
        var flows = host.Workspace.Flows;

        if (sets.Count == 0 && flows.Count == 0)
        {
            console.MarkupLine("[dim]No test sets or flows in this workspace.[/]");
            return ExitCode.Ok;
        }

        if (sets.Count > 0)
        {
            console.MarkupLine("[bold]Test sets[/]");
            foreach (var set in sets)
            {
                var name = set.Name ?? TargetResolver.Stem(set.RelativePath);
                console.MarkupLine($"  {Markup.Escape(name)}  [dim]{set.Tests.Count} tests · {Markup.Escape(set.RelativePath)}[/]");
            }
        }

        if (flows.Count > 0)
        {
            if (sets.Count > 0) console.WriteLine();
            console.MarkupLine("[bold]Flows[/]");
            foreach (var flow in flows)
            {
                var name = flow.Name ?? TargetResolver.Stem(flow.RelativePath);
                console.MarkupLine($"  {Markup.Escape(name)}  [dim]{flow.Steps.Count} steps · {Markup.Escape(flow.RelativePath)}[/]");
            }
        }

        return ExitCode.Ok;
    }
}
