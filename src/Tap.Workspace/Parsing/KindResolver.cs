using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Maps filename suffix → <see cref="WorkspaceKind"/> per spec §2. The filename suffix is
/// canonical; the <c>kind:</c> frontmatter field must match or the parser raises
/// <see cref="WorkspaceErrorCode.E_KIND_MISMATCH"/>.
/// </summary>
public static class KindResolver
{
    public static WorkspaceKind? FromFileName(string fileName)
    {
        if (fileName.Equals("tap.md", StringComparison.OrdinalIgnoreCase))
            return WorkspaceKind.Workspace;

        if (fileName.Equals("_collection.md", StringComparison.OrdinalIgnoreCase))
            return WorkspaceKind.Collection;

        if (fileName.EndsWith(".req.md", StringComparison.OrdinalIgnoreCase))
            return WorkspaceKind.Request;
        if (fileName.EndsWith(".auth.md", StringComparison.OrdinalIgnoreCase))
            return WorkspaceKind.Auth;
        if (fileName.EndsWith(".env.md", StringComparison.OrdinalIgnoreCase))
            return WorkspaceKind.Env;

        return null;
    }

    public static string FrontmatterValue(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Request => "request",
        WorkspaceKind.Auth => "auth",
        WorkspaceKind.Env => "env",
        WorkspaceKind.Collection => "collection",
        WorkspaceKind.Workspace => "workspace",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static WorkspaceKind? ParseFrontmatterValue(string value) => value switch
    {
        "request" => WorkspaceKind.Request,
        "auth" => WorkspaceKind.Auth,
        "env" => WorkspaceKind.Env,
        "collection" => WorkspaceKind.Collection,
        "workspace" => WorkspaceKind.Workspace,
        _ => null,
    };
}
