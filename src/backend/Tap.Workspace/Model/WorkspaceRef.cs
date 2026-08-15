namespace Tap.Workspace.Model;

/// <summary>
/// A normalized reference to another workspace file. Frontmatter accepts either a
/// relative path (<c>../../apis/stripe.api.md</c>) or an id reference (<c>id:0192-...</c>);
/// the parser canonicalizes both into this shape. See spec §14.
/// </summary>
public sealed record WorkspaceRef
{
    /// <summary>Workspace-relative path to the file, normalized with forward slashes. Null if the ref was an id with no current owner.</summary>
    public string? RelativePath { get; init; }

    /// <summary>Stable id of the target file (UUIDv7). Always populated once the workspace index has been built.</summary>
    public string? Id { get; init; }

    /// <summary>The exact string the user wrote, for round-tripping back to disk on save.</summary>
    public required string SourceText { get; init; }

    public static WorkspaceRef FromPath(string relativePath) => new()
    {
        RelativePath = NormalizePath(relativePath),
        SourceText = relativePath,
    };

    public static WorkspaceRef FromId(string id) => new()
    {
        Id = id,
        SourceText = "id:" + id,
    };

    private static string NormalizePath(string p) => p.Replace('\\', '/');
}
