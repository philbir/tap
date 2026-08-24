using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Agent;

/// <summary>
/// Enforces a collection's <c>agent:</c> option on the agent surfaces. Every sanctioned
/// agent path routes through here — MCP <c>describe_request</c>/<c>send_request</c>, the
/// dynamic factory, and inventory filtering — so the option means one thing everywhere.
///
/// <para>Deliberately not enforced in the engine itself: <c>send</c>, the Studio UI, and
/// <c>run_test</c> (a curated, author-written suite is the author's traffic, not the
/// agent's) stay unaffected. This is policy for agents, not a sandbox — a note in
/// CLAUDE.md's terms, "what the sanctioned surfaces will do".</para>
/// </summary>
public static class AgentAccess
{
    public static bool IsEnabled(CollectionFile? collection) => collection?.Agent.Enabled ?? true;

    /// <summary>Whether agents may use <paramref name="request"/> — i.e. its owning
    /// collection (if any) has not opted out. Requests outside any collection are allowed.</summary>
    public static bool IsEnabled(LoadedWorkspace workspace, RequestFile request)
        => IsEnabled(CollectionLocator.ForRequest(workspace, request));

    public static void EnsureAllowed(CollectionFile collection)
    {
        if (collection.Agent.Enabled) return;
        throw new WorkspaceParseException(new WorkspaceError(
            WorkspaceErrorCode.E_AGENT_ACCESS_DISABLED,
            $"Collection '{collection.Name ?? collection.RelativePath}' has agent access disabled "
            + $"(agent: false in its {KindResolver.CollectionFileName}). Ask the user to enable it if this call is wanted.",
            collection.RelativePath));
    }

    public static void EnsureAllowed(LoadedWorkspace workspace, RequestFile request)
    {
        var collection = CollectionLocator.ForRequest(workspace, request);
        if (collection is not null) EnsureAllowed(collection);
    }
}
