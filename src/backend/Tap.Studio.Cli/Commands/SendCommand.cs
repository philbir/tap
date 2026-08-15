using System.ComponentModel;
using Spectre.Console.Cli;
using Tap.Execution;
using Tap.Execution.Contracts;
using Tap.Execution.Testing;
using Tap.Execution.Workspace;
using Tap.Studio.Cli.Auth;
using Tap.Studio.Cli.Output;
using Tap.Studio.Cli.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio send &lt;name|path&gt;</c> — fire one request and report its assertions.
///
/// <para>Implemented as a one-entry run rather than its own execution path. A request sent by
/// <c>send</c> and the same request sent as a test have to behave identically, and the surest
/// way to guarantee that is for them to be the same code.</para>
/// </summary>
public sealed class SendCommand : AsyncCommand<SendCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Request: a workspace-relative path, the name: from its frontmatter, or its filename stem.")]
        public string Target { get; init; } = string.Empty;

        [CommandOption("--use-cached-tokens")]
        [Description("Allow ~/.tap/auth-tokens.json to satisfy auth.")]
        public bool UseCachedTokens { get; init; }

        [CommandOption("--body")]
        [Description("Print the response body.")]
        public bool ShowBody { get; init; }

        [CommandOption("--json")]
        [Description("Print the step result as JSON on stdout, secrets redacted. Progress and errors go to stderr.")]
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

        if (!TargetResolver.TryResolve(
                host.Workspace, settings.Target, [WorkspaceKind.Request], out var target, out var resolveError))
        {
            reporter.Error(resolveError);
            return ExitCode.UsageError;
        }

        if (!VariableInputs.TryCollect(settings.VarFiles, settings.Vars, out var variables, out var varError))
        {
            reporter.Error(varError);
            return ExitCode.UsageError;
        }

        host.WithTokens(h => HeadlessAuthTokenSource.Create(
            h.Workspace, h.CreateRegistry, http, h.RootDirectory, settings.UseCachedTokens));

        var envPath = settings.Env is { Length: > 0 }
            && TargetResolver.TryResolve(host.Workspace, settings.Env!, [WorkspaceKind.Env], out var env, out _)
                ? env.Path
                : null;
        if (settings.Env is { Length: > 0 } && envPath is null)
        {
            reporter.Error($"No environment matches '{settings.Env}'.");
            return ExitCode.UsageError;
        }

        ExecutionIdentity.UserAgent = $"tap-studio-cli/{ExecutionIdentity.Version}";

        // A synthetic single-entry test set, so `send` goes down the same road a run does.
        var request = new RunTestRequestDto(
            Path: target.Path,
            Env: envPath,
            Stage: settings.Stage,
            Only: null,
            Overrides: variables.Count > 0 ? variables : null);

        try
        {
            // The redactor rides along on the rendered request; captured here so the JSON
            // echo can be scrubbed of everything secret that went into this render.
            var redactor = SecretRedactor.None;
            var runner = new TestRunner(new RequestPipeline(host))
            {
                OnRendered = rendered => redactor = rendered.Redactor,
            };
            var step = await runner.SendAsync(request, ct);

            if (settings.Json) JsonOutput.Write(step, redactor);
            else StepPrinter.Print(console, step, settings.ShowBody || settings.Verbose);
            return step.Ok ? ExitCode.Ok : ExitCode.TestFailed;
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
        catch (WorkspaceParseException ex)
        {
            reporter.Error($"{ex.Error.Code} {ex.Error.RelativePath ?? target.Path}: {ex.Error.Message}");
            return ExitCode.WorkspaceError;
        }
    }

}
