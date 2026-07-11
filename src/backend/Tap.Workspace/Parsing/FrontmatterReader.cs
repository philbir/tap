using YamlDotNet.RepresentationModel;
using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Splits a workspace file into its YAML frontmatter and Markdown body. See spec §3.
///
/// Rules:
/// - The file MUST begin with a line that is exactly <c>---</c>.
/// - Frontmatter ends at the next line that is exactly <c>---</c>.
/// - Empty frontmatter (no content between the fences) is illegal — <c>kind:</c> is mandatory.
/// </summary>
internal static class FrontmatterReader
{
    public sealed record Split(YamlMappingNode Frontmatter, string Body, int BodyStartLine);

    public static Split Read(string content, string relativePath)
    {
        using var reader = new StringReader(content);
        var first = reader.ReadLine();
        if (first is null || first.TrimEnd('\r') != "---")
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_FRONTMATTER_MISSING,
                "File must start with a YAML frontmatter fence (---).",
                relativePath,
                1));
        }

        var yamlLines = new List<string>();
        int line = 1;
        string? l;
        var foundClose = false;
        while ((l = reader.ReadLine()) is not null)
        {
            line++;
            if (l.TrimEnd('\r') == "---")
            {
                foundClose = true;
                break;
            }
            yamlLines.Add(l);
        }

        if (!foundClose)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_FRONTMATTER_MISSING,
                "Frontmatter was opened with --- but never closed.",
                relativePath));
        }

        if (yamlLines.Count == 0)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_KIND_MISSING,
                "Empty frontmatter. At minimum 'kind:' must be present.",
                relativePath,
                1));
        }

        var yamlText = string.Join('\n', yamlLines);
        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var ys = new StringReader(yamlText);
            stream.Load(ys);
        }
        catch (Exception ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                ex.Message,
                relativePath));
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                "Frontmatter must be a YAML mapping at the top level.",
                relativePath));
        }

        var body = reader.ReadToEnd() ?? string.Empty;
        return new Split(root, body, line + 1);
    }
}
