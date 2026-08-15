namespace Tap.Workspace.Model;

/// <summary>
/// The file kinds defined by <c>docs/workspace-format.md</c> §2. The filename suffix is canonical
/// (<c>*.req.md</c>, <c>*.auth.md</c>, <c>*.env.md</c>, <c>*.flow.md</c>, <c>*.test.md</c>,
/// <c>_collection.md</c>, <c>tap.md</c>); the <c>kind:</c> frontmatter field must agree with the
/// suffix or parsing fails with <see cref="WorkspaceErrorCode.E_KIND_MISMATCH"/>. Collections own
/// a baseUrl, optional stages, and default headers/auth — they replace the older <c>api</c> kind
/// that used to live in a separate <c>apis/</c> directory.
/// </summary>
public enum WorkspaceKind
{
    Request,
    Auth,
    Env,
    Collection,
    Workspace,

    /// <summary>An ordered sequence of requests that passes values between steps (§10).</summary>
    Flow,

    /// <summary>A named group of checks, each running one request or one flow (§11).</summary>
    Test,
}
