using Tap.Execution.Workspace;
using Tap.Studio.Cli.Auth;
using Tap.Studio.Cli.Workspace;
using Tap.Studio.Mcp;

namespace Tap.Studio.Cli.Mcp;

/// <summary>
/// The stdio server's side of <see cref="IMcpWorkspaceProvider"/>: the workspace root the
/// server was started for, and headless auth. Each tool call loads the workspace fresh — an
/// MCP session is long-lived and the agent on the other end edits workspace files between
/// calls, so a load-once snapshot would run yesterday's files. <see cref="CliWorkspaceHost"/>
/// is deliberately snapshot-per-load; per-call loading is what makes that model fit a server.
/// </summary>
public sealed class McpRuntime(string workspaceRoot, bool useCachedTokens) : IMcpWorkspaceProvider
{
    private readonly HttpClient _http = new();

    public string WorkspaceRoot => workspaceRoot;

    public IWorkspaceHost GetHost()
        => CliWorkspaceHost.Load(workspaceRoot)
            .WithTokens(h => HeadlessAuthTokenSource.Create(
                h.Workspace, h.CreateRegistry, _http, h.RootDirectory, useCachedTokens));
}
