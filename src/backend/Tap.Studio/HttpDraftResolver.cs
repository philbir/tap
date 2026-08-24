using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Studio;

/// <summary>
/// Picks one request out of the unsaved text of a <c>.http</c> file.
///
/// <para><b>Why this exists.</b> Every structured kind reaches the executor as a
/// <c>RequestSpecDto</c> that the Studio emits and re-parses, so an unsaved draft and a saved
/// file take the same road. A <c>.http</c> file has no spec — the raw text <em>is</em> the
/// document, and Tap never rewrites it — so its draft arrives as text and is parsed instead of
/// emitted-then-parsed. Same principle, one fewer step.</para>
///
/// <para><b>Identity comes from the draft, not from disk.</b> A request's name is derived from
/// its URL when the file doesn't name it, so editing a request line renames it; adding a request
/// creates one that has never existed on disk. Matching the fragment against the freshly-parsed
/// text — the same text the editor parsed to build the list the user clicked — is what keeps the
/// two ends agreeing about which request is which. Matching against the workspace model instead
/// would fail on exactly the edits this feature exists to support.</para>
/// </summary>
public static class HttpDraftResolver
{
    /// <param name="requestPath">A fragment path (<c>orders.http#get-order</c>), or the bare file
    /// path when the file holds exactly one request — the same two spellings
    /// <see cref="Tap.Execution.Agent.TargetResolver"/> accepts.</param>
    /// <param name="draftSource">The unsaved text of that file.</param>
    /// <exception cref="WorkspaceParseException">The draft does not parse, or does not contain the
    /// request that was asked for.</exception>
    public static RequestFile Resolve(string requestPath, string draftSource)
    {
        var (filePath, fragment) = HttpFragment.Split(requestPath);
        if (!KindResolver.IsHttpFileName(filePath))
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_KIND_MISMATCH,
                $"'{filePath}' is not a .http file, so it has no raw draft to execute.",
                filePath));

        var parsed = HttpFileParser.Parse(filePath, draftSource);
        // Warnings (constructs belonging to another tool's dialect) must not block a send. They
        // don't stop the file loading from disk either, and the request being sent is usually not
        // even the one that produced them.
        if (parsed.Errors.FirstOrDefault(e => e.Severity == WorkspaceErrorSeverity.Error) is { } failure)
            throw new WorkspaceParseException(failure);

        if (fragment is null)
        {
            if (parsed.Requests.Count == 1) return parsed.Requests[0];
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DANGLING_REF,
                $"'{filePath}' holds {parsed.Requests.Count} requests — address one with a '#name' fragment.",
                filePath));
        }

        var match = parsed.Requests.FirstOrDefault(r =>
            string.Equals(HttpFragment.Split(r.RelativePath).Fragment, fragment, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            // Naming the survivors matters here: the usual cause is that the edit in progress
            // renamed the request out from under the list, and the new name is the answer.
            var available = parsed.Requests.Count == 0
                ? "it now has none"
                : "it now has: " + string.Join(", ", parsed.Requests
                    .Select(r => "#" + HttpFragment.Split(r.RelativePath).Fragment));
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_DANGLING_REF,
                $"The edited file has no request '#{fragment}' — {available}.",
                filePath));
        }
        return match;
    }
}
