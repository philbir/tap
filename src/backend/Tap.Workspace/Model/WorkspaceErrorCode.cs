namespace Tap.Workspace.Model;

/// <summary>
/// Canonical parse / render error codes — mirrors §14 of <c>docs/workspace-format.md</c>.
/// Each code is stable and forms part of the public API; tooling matches on the string.
/// </summary>
public static class WorkspaceErrorCode
{
    public const string E_FRONTMATTER_MISSING = nameof(E_FRONTMATTER_MISSING);
    public const string E_FRONTMATTER_MALFORMED_YAML = nameof(E_FRONTMATTER_MALFORMED_YAML);
    public const string E_KIND_MISSING = nameof(E_KIND_MISSING);
    public const string E_KIND_MISMATCH = nameof(E_KIND_MISMATCH);
    public const string E_UNKNOWN_FIELD = nameof(E_UNKNOWN_FIELD);
    public const string E_NO_REQUEST_BLOCK = nameof(E_NO_REQUEST_BLOCK);
    public const string E_MULTIPLE_REQUEST_BLOCKS = nameof(E_MULTIPLE_REQUEST_BLOCKS);
    public const string E_DANGLING_REF = nameof(E_DANGLING_REF);
    public const string E_VAR_UNKNOWN = nameof(E_VAR_UNKNOWN);
    public const string E_VAR_CYCLE = nameof(E_VAR_CYCLE);
    public const string E_UNKNOWN_PROVIDER = nameof(E_UNKNOWN_PROVIDER);
    public const string E_PROVIDER_RESOLUTION_FAILED = nameof(E_PROVIDER_RESOLUTION_FAILED);
    public const string E_PROVIDER_CONFIG_INVALID = nameof(E_PROVIDER_CONFIG_INVALID);
    public const string E_PROVIDER_NOT_WRITABLE = nameof(E_PROVIDER_NOT_WRITABLE);
    public const string E_PROVIDER_DECRYPT_FAILED = nameof(E_PROVIDER_DECRYPT_FAILED);
    public const string E_AUTH_TYPE_INVALID = nameof(E_AUTH_TYPE_INVALID);
    public const string E_HTTP_BLOCK_SYNTAX = nameof(E_HTTP_BLOCK_SYNTAX);

    /// <summary>An entry under <c>assertions:</c> is not a usable (extractor, matcher) pair —
    /// zero or several extractors, several matchers, an unknown key, or an operator that can
    /// never apply to the chosen extractor. See §5.5 of <c>docs/workspace-format.md</c>.</summary>
    public const string E_ASSERT_INVALID = nameof(E_ASSERT_INVALID);

    /// <summary>A flow has no steps, or an entry under <c>steps:</c> is not usable — no
    /// <c>request:</c>, an extraction with zero or several sources, an unknown key. See §10 of
    /// <c>docs/workspace-format.md</c>.</summary>
    public const string E_FLOW_INVALID = nameof(E_FLOW_INVALID);

    /// <summary>A test set has no tests, or an entry under <c>tests:</c> is not usable —
    /// neither or both of <c>request:</c>/<c>flow:</c>, an unknown key. See §11 of
    /// <c>docs/workspace-format.md</c>.</summary>
    public const string E_TEST_INVALID = nameof(E_TEST_INVALID);

    /// <summary>The folder walk hit its time / folder-count budget, so the workspace is only
    /// partially loaded. Almost always means the root is far too broad — see
    /// <see cref="Tap.Workspace.WorkspaceLoader.ScanBudget"/>.</summary>
    public const string E_WORKSPACE_SCAN_TRUNCATED = nameof(E_WORKSPACE_SCAN_TRUNCATED);

    /// <summary>The workspace could not be read at all (missing folder, unreadable root). The
    /// workspace loads empty carrying this error rather than taking the host down with it.</summary>
    public const string E_WORKSPACE_LOAD_FAILED = nameof(E_WORKSPACE_LOAD_FAILED);

    /// <summary>A dynamic (agent-supplied) request tried to leave its collection: the URL was
    /// absolute, or rendered absolute through a variable, without the caller explicitly
    /// allowing that. The guard exists because a dynamic URL combined with inherited auth
    /// headers is a credential-exfiltration primitive — see <c>Tap.Execution.Agent</c>.</summary>
    public const string E_DYNAMIC_URL_NOT_COLLECTION_SCOPED = nameof(E_DYNAMIC_URL_NOT_COLLECTION_SCOPED);

    /// <summary>A dynamic request named a collection that doesn't exist, or its method / URL /
    /// headers were malformed (empty, or carrying line breaks that would smuggle extra lines
    /// into the synthesized http block).</summary>
    public const string E_DYNAMIC_REQUEST_INVALID = nameof(E_DYNAMIC_REQUEST_INVALID);

    /// <summary>An agent surface tried to use a collection whose <c>agent:</c> option
    /// disables agent access. Policy set by the collection's author in
    /// <c>_collection.md</c>; the human-facing Studio and CLI commands ignore it.</summary>
    public const string E_AGENT_ACCESS_DISABLED = nameof(E_AGENT_ACCESS_DISABLED);
}

public sealed record WorkspaceError(
    string Code,
    string Message,
    string? RelativePath = null,
    int? Line = null);

public sealed class WorkspaceParseException(WorkspaceError error)
    : Exception($"{error.Code}: {error.Message}" + (error.RelativePath is null ? "" : $" ({error.RelativePath}{(error.Line is null ? "" : $":{error.Line}")})"))
{
    public WorkspaceError Error { get; } = error;
}
