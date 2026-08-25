using YamlDotNet.RepresentationModel;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Read access to the two halves of a workspace file — the YAML frontmatter mapping and the
/// Markdown body under it — for callers that mean to edit one key and write the file back.
///
/// <para><see cref="FrontmatterReader"/> stays internal because parsing a file all the way to a
/// <see cref="Model.WorkspaceFile"/> is the only thing the loader wants. Patching one is a
/// different job: the Studio declares a single <c>vars:</c> entry into a file it is not
/// otherwise editing, and it must not re-derive the fence rules (or the untrusted-YAML screen)
/// to do it. This is that seam and nothing more — the emitters under <c>Tap.Studio/Specs/</c>
/// remain the only producers of Tap's YAML.</para>
/// </summary>
public static class WorkspaceFileText
{
    /// <param name="Frontmatter">The parsed frontmatter mapping. Mutable: patch it and re-emit.</param>
    /// <param name="Body">Everything after the closing fence, verbatim — including any fenced
    /// <c>http</c> block, which is part of the body as far as this split is concerned.</param>
    public sealed record Parts(YamlMappingNode Frontmatter, string Body);

    /// <summary>
    /// Splits <paramref name="content"/> at its frontmatter fences.
    /// </summary>
    /// <exception cref="Model.WorkspaceParseException">The file has no frontmatter, leaves it
    /// unclosed, or the YAML between the fences is malformed or unsafe.</exception>
    public static Parts Split(string content, string relativePath)
    {
        var split = FrontmatterReader.Read(content, relativePath);
        return new Parts(split.Frontmatter, split.Body);
    }
}
