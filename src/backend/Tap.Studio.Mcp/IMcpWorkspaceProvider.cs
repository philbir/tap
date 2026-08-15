using Tap.Execution.Workspace;

namespace Tap.Studio.Mcp;

/// <summary>
/// Where the MCP tools get a workspace for one call — the only seam between the shared tool
/// layer and its two hosts, because it is the only thing they genuinely disagree about. The
/// CLI's stdio server loads the folder fresh per call and mints tokens headlessly; the
/// Studio's <c>/mcp</c> endpoint hands back its live watched snapshot whose token source is
/// the cache the user fills by signing in through a browser — which is what lets an agent
/// run requests behind interactive OAuth without a credential ever leaving the Studio
/// process.
/// </summary>
public interface IMcpWorkspaceProvider
{
    IWorkspaceHost GetHost();
}
