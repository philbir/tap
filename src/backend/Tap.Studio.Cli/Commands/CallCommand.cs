using System.ComponentModel;
using Spectre.Console.Cli;
using Tap.Execution;
using Tap.Execution.Agent;
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
/// <c>tap-studio call GET /users/42 --collection demo</c> — send an ad-hoc request through an
/// existing collection, inheriting its baseUrl, default headers, variables, and auth.
///
/// <para>The agent-facing counterpart of <c>send</c>: the caller gets a fully-authenticated
/// request without ever holding a credential, and in return the URL must stay inside the
/// collection (relative, joined onto its baseUrl) unless <c>--allow-any-url</c> says
/// otherwise — with inherited auth attached, "send my token wherever the URL points" is not a
/// default anyone should get by accident.</para>
/// </summary>
public sealed class CallCommand : AsyncCommand<CallCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<METHOD>")]
        [Description("HTTP method: GET, POST, PUT, PATCH, DELETE, …")]
        public string Method { get; init; } = string.Empty;

        [CommandArgument(1, "<URL>")]
        [Description("Path to hit, relative to the collection's baseUrl (e.g. /users/42). Absolute URLs need --allow-any-url.")]
        public string Url { get; init; } = string.Empty;

        [CommandOption("-c|--collection <NAME|PATH>")]
        [Description("The collection carrying the request: its name, its directory, or its _collection.md path.")]
        public string? Collection { get; init; }

        [CommandOption("-H|--header <HEADER>")]
        [Description("Extra header as 'Name: value'. Repeatable.")]
        public string[]? Headers { get; init; }

        [CommandOption("-d|--data <TEXT|@FILE>")]
        [Description("Request body. @path reads the body from a file.")]
        public string? Data { get; init; }

        [CommandOption("--auth <REF>")]
        [Description("Auth profile ref, relative to the collection directory. Defaults to the collection's own default auth.")]
        public string? Auth { get; init; }

        [CommandOption("--name <LABEL>")]
        [Description("Display name for the result. Defaults to 'METHOD url'.")]
        public string? Name { get; init; }

        [CommandOption("--allow-any-url")]
        [Description("Permit an absolute URL outside the collection's baseUrl. Off by default: the request carries the collection's credentials.")]
        public bool AllowAnyUrl { get; init; }

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

        if (string.IsNullOrWhiteSpace(settings.Collection))
        {
            reporter.Error("--collection is required: a dynamic request runs inside a collection so it can inherit the baseUrl and auth.");
            return ExitCode.UsageError;
        }

        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            reporter.Error(locateError);
            return ExitCode.WorkspaceError;
        }

        if (!VariableInputs.TryCollect(settings.VarFiles, settings.Vars, out var variables, out var varError))
        {
            reporter.Error(varError);
            return ExitCode.UsageError;
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var raw in settings.Headers ?? [])
        {
            if (!HeaderArgument.TryParse(raw, out var header, out var headerError))
            {
                reporter.Error(headerError);
                return ExitCode.UsageError;
            }
            headers.Add(header);
        }

        string? body = settings.Data;
        if (body is ['@', .. var bodyPath])
        {
            try
            {
                body = await File.ReadAllTextAsync(bodyPath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reporter.Error($"Could not read body file '{bodyPath}': {ex.Message}");
                return ExitCode.UsageError;
            }
        }

        using var http = new HttpClient();
        var host = CliWorkspaceHost.Load(root);

        RequestFile requestFile;
        try
        {
            requestFile = DynamicRequestFactory.Create(host.Workspace, settings.Collection!, new DynamicRequestSpec
            {
                Method = settings.Method,
                Url = settings.Url,
                Headers = headers.Count > 0 ? headers : null,
                Body = body,
                Auth = settings.Auth,
                Name = settings.Name,
                AllowAnyUrl = settings.AllowAnyUrl,
            });
        }
        catch (WorkspaceParseException ex)
        {
            reporter.Error(ex.Error.Message);
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

        var request = new RunTestRequestDto(
            Path: requestFile.RelativePath,
            Env: envPath,
            Stage: settings.Stage,
            Only: null,
            Overrides: variables.Count > 0 ? variables : null);

        try
        {
            var redactor = SecretRedactor.None;
            var runner = new TestRunner(new RequestPipeline(host))
            {
                // Runs after render, before send: capture the redactor for the echo, and hold
                // the collection-scope line — a throw here fails the step without sending it.
                OnRendered = rendered =>
                {
                    redactor = rendered.Redactor;
                    DynamicRequestFactory.EnsureCollectionScoped(rendered, settings.AllowAnyUrl);
                },
            };
            var step = await runner.SendAsync(requestFile, request, ct);

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
            reporter.Error($"{ex.Error.Code} {ex.Error.RelativePath ?? requestFile.RelativePath}: {ex.Error.Message}");
            return ExitCode.WorkspaceError;
        }
    }
}

/// <summary>Parses one <c>-H 'Name: value'</c> argument. Split on the first colon, exactly as
/// the header would be written on the wire.</summary>
public static class HeaderArgument
{
    public static bool TryParse(string raw, out KeyValuePair<string, string> header, out string error)
    {
        header = default;
        error = string.Empty;

        var colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            error = $"--header '{raw}' is not in 'Name: value' form.";
            return false;
        }

        var name = raw[..colon].Trim();
        if (name.Length == 0)
        {
            error = $"--header '{raw}' has an empty name.";
            return false;
        }

        header = new KeyValuePair<string, string>(name, raw[(colon + 1)..].Trim());
        return true;
    }
}
