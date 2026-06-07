using Markdig;
using Markdig.Syntax;
using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Walks the Markdown body of a request file and locates the single fenced
/// <c>http</c> block. Spec §5.2: exactly one block is required; zero or multiple
/// produce errors.
/// </summary>
internal static class HttpBlockExtractor
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    public sealed record Located(string Content, int StartLine);

    public static Located Extract(string body, string relativePath)
    {
        var doc = Markdown.Parse(body, Pipeline);
        Located? found = null;
        foreach (var node in doc.Descendants())
        {
            if (node is not FencedCodeBlock fenced) continue;
            var info = (fenced.Info ?? string.Empty).Trim();
            if (!info.Equals("http", StringComparison.OrdinalIgnoreCase)) continue;

            var content = fenced.Lines.ToString();
            if (found is not null)
            {
                throw new WorkspaceParseException(new WorkspaceError(
                    WorkspaceErrorCode.E_MULTIPLE_REQUEST_BLOCKS,
                    "Request file must contain exactly one fenced 'http' block; found more than one.",
                    relativePath,
                    fenced.Line + 1));
            }
            found = new Located(content, fenced.Line + 1);
        }

        if (found is null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_NO_REQUEST_BLOCK,
                "Request file must contain a fenced 'http' block (``` http ... ```).",
                relativePath));
        }

        return found;
    }
}
