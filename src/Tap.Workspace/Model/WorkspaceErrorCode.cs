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
