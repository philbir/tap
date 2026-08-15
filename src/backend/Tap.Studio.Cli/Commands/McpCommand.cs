using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Tap.Execution;
using Tap.Studio.Cli.Mcp;
using Tap.Studio.Cli.Workspace;
using Tap.Studio.Mcp;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio mcp</c> — serve the agent surface over the Model Context Protocol on
/// stdio. Registered in a client's MCP config as command <c>tap-studio</c>, args
/// <c>["mcp", "--workspace", "&lt;dir&gt;"]</c>.
///
/// <para>stdout belongs to the JSON-RPC protocol from the moment this starts — everything
/// human-facing (host logs included) is forced to stderr, because a single stray stdout
/// line corrupts the session.</para>
/// </summary>
public sealed class McpCommand : AsyncCommand<McpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-w|--workspace <DIR>")]
        [Description("Workspace directory to serve. Defaults to the nearest ancestor containing tap.md, or the first workspace found beneath the working directory.")]
        public string? WorkspaceDirectory { get; init; }

        [CommandOption("--use-cached-tokens")]
        [Description("Allow ~/.tap/auth-tokens.json to satisfy auth — lets tools run requests behind interactive OAuth the user already signed into via the Studio.")]
        public bool UseCachedTokens { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        // Fail before the protocol starts: a server that comes up and then errors every tool
        // call because there is no workspace helps nobody.
        if (!WorkspaceLocator.TryLocate(settings.WorkspaceDirectory, null, out var root, out var locateError))
        {
            Console.Error.WriteLine(locateError);
            return ExitCode.WorkspaceError;
        }

        ExecutionIdentity.UserAgent = $"tap-studio-mcp/{ExecutionIdentity.Version}";

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<IMcpWorkspaceProvider>(new McpRuntime(root, settings.UseCachedTokens));
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<TapStudioTools>();

        await builder.Build().RunAsync(ct);
        return ExitCode.Ok;
    }
}
