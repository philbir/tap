using Tap.Execution.Workspace;

namespace Tap.Studio.Mcp;

/// <summary>
/// The Studio's side of <see cref="IMcpWorkspaceProvider"/>: hand the tools the live
/// <see cref="WorkspaceService"/>. That one line is the whole reason the <c>/mcp</c>
/// endpoint exists — the service's token source is the cache the user fills by signing in
/// through a browser, so an agent can run requests behind interactive OAuth (PKCE) while
/// the token never leaves this process; and its workspace is the watched snapshot, so a
/// file the user just saved in the editor is what the next tool call runs.
/// </summary>
public sealed class StudioMcpProvider(WorkspaceService workspace) : IMcpWorkspaceProvider
{
    public IWorkspaceHost GetHost() => workspace;
}
