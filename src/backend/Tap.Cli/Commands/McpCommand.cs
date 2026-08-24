using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Tap.Cli.Mcp;
using Tap.Core.Capture;
using Tap.Inspector.Mcp;

namespace Tap.Cli.Commands;

/// <summary>
/// <c>tap mcp</c> — serve a running inspector's captured traffic over the Model Context
/// Protocol on stdio. Registered in a client's MCP config as command <c>tap</c>, args
/// <c>["mcp"]</c> — it finds the running inspector itself.
///
/// <para>This is a host, not an implementation: the tools live in <c>Tap.Inspector.Mcp</c> and
/// are served identically by the inspector's own <c>/mcp</c> endpoint. What this adds is stdio
/// — which every MCP client supports, and streamable HTTP does not universally.</para>
///
/// <para>The provider talks to the inspector's redacted <c>/api/agent/*</c> surface, so
/// nothing raw crosses into this process. stdout belongs to the JSON-RPC protocol from the
/// moment this starts; everything human-facing is forced to stderr, because a single stray
/// stdout line corrupts the session.</para>
/// </summary>
public sealed class McpCommand : AsyncCommand<McpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-u|--url <URL>")]
        [Description("Base URL of the running inspector's UI port. Omit to discover the most recently started inspector.")]
        public string? Url { get; init; }

        [CommandOption("-p|--ui-port <PORT>")]
        [Description("UI port of the inspector to connect to. Omit to discover the most recently started one.")]
        public int? UiPort { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        // The inspector publishes a handle when agent access is on: where it is, and this run's
        // token. Discovery beats making someone paste a credential, and the file's 0600
        // permissions are the actual authorization — see AgentBridgeFile.
        var handle = settings.UiPort is { } port
            ? AgentBridgeFile.Read(port)
            : AgentBridgeFile.Discover();

        if (handle is null && settings.Url is null)
        {
            Console.Error.WriteLine(
                "No running inspector found with agent access enabled." + Environment.NewLine +
                $"Looked in {AgentBridgeFile.DefaultRootDirectory}." + Environment.NewLine +
                "Start one with Inspector__Agent__Enabled=true, or call .WithAgentAccess() on the tap " +
                "in your AppHost, then run this again.");
            return 2;
        }

        var url = settings.Url ?? handle!.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            Console.Error.WriteLine($"'{url}' is not an absolute URL.");
            return 2;
        }

        if (handle is null)
        {
            Console.Error.WriteLine(
                $"No bridge handle for {baseUri}. Connecting without a token; the inspector will " +
                "refuse unless agent access is on and this is the right port.");
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddHttpClient<IMcpCaptureProvider, HttpCaptureProvider>(http =>
        {
            http.BaseAddress = new Uri(baseUri, "/");
            if (handle is not null) http.DefaultRequestHeaders.Add(AgentBridgeFile.HeaderName, handle.Token);

            // wait_for_request parks for up to five minutes on purpose. The per-call deadline
            // in the provider is what actually bounds a wait; this only has to not fire first.
            http.Timeout = TimeSpan.FromMinutes(6);
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<TapInspectorTools>();

        await builder.Build().RunAsync(ct);
        return 0;
    }
}
