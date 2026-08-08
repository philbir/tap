using YamlDotNet.Core;
using YamlDotNet.Core.Events;
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
    /// <summary>
    /// Ceiling on frontmatter size. A workspace is whatever the developer cloned, so the YAML
    /// between the fences is untrusted; the cap keeps parse cost proportional to something sane.
    /// </summary>
    internal const int MaxFrontmatterChars = 256 * 1024;

    /// <summary>
    /// Ceiling on frontmatter nesting. <c>YamlStream.Load</c> builds the representation model with
    /// recursive descent, so a few thousand nested flow collections overflow the stack — and a
    /// <c>StackOverflowException</c> cannot be caught, it just takes the host down. Real frontmatter
    /// is four or five levels deep.
    /// </summary>
    private const int MaxFrontmatterDepth = 64;

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
        if (yamlText.Length > MaxFrontmatterChars)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                $"Frontmatter is {yamlText.Length} characters; the limit is {MaxFrontmatterChars}.",
                relativePath,
                1));
        }

        ScreenUntrustedYaml(yamlText, relativePath);

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

    /// <summary>
    /// Screens frontmatter for the two shapes that turn a cloned repo into a denial of service,
    /// before the text reaches the representation model.
    ///
    /// <para><b>Anchors and aliases</b> are rejected outright. Nothing Tap writes uses them — every
    /// emitter goes through <c>SpecYaml</c>, which saves with <c>assignAnchors: false</c> — but a
    /// mapping whose <i>key</i> aliases a deeply nested anchor forces YamlDotNet to hash and compare
    /// the fully expanded graph while building <c>YamlMappingNode.Children</c>. Measured against the
    /// pinned YamlDotNet 18.1.0 that is roughly 9x per nesting level: 425 bytes took 1.6 s, 520 bytes
    /// took 105 s. The loader walks every <c>*.md</c> eagerly at startup, so one such file wedges the
    /// host before it ever listens.</para>
    ///
    /// <para><b>Excessive nesting</b> is rejected because the representation model is built by
    /// recursive descent — deep enough input overflows the stack, which no <c>catch</c> can save.</para>
    ///
    /// <para>The scan runs on the low-level event stream, which is linear in the input and never
    /// expands an alias — so the guard cannot itself be turned into the bomb it looks for.</para>
    ///
    /// <para>Internal rather than private so any other loader of workspace-controlled YAML can run
    /// the same screen before calling <c>YamlStream.Load</c>.</para>
    /// </summary>
    internal static void ScreenUntrustedYaml(string yamlText, string relativePath)
    {
        var parser = new Parser(new StringReader(yamlText));
        var depth = 0;
        try
        {
            while (parser.MoveNext())
            {
                var current = parser.Current;
                if (current is null) continue;

                // Marks are 1-based within the frontmatter; +1 puts them back on the file's own
                // line numbering, which starts one line below the opening fence.
                var line = (int)(current.Start.Line + 1);

                if (current is AnchorAlias)
                {
                    throw new WorkspaceParseException(new WorkspaceError(
                        WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                        "Frontmatter uses a YAML alias (*). Anchors and aliases are not supported in Tap workspace files.",
                        relativePath,
                        line));
                }

                if (current is NodeEvent node && !node.Anchor.IsEmpty)
                {
                    throw new WorkspaceParseException(new WorkspaceError(
                        WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                        "Frontmatter declares a YAML anchor (&). Anchors and aliases are not supported in Tap workspace files.",
                        relativePath,
                        line));
                }

                depth += current.NestingIncrease;
                if (depth > MaxFrontmatterDepth)
                {
                    throw new WorkspaceParseException(new WorkspaceError(
                        WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                        $"Frontmatter nests more than {MaxFrontmatterDepth} levels deep.",
                        relativePath,
                        line));
                }
            }
        }
        catch (YamlException)
        {
            // Malformed YAML — leave the diagnostic to the real load below, which reports the same
            // error code with YamlDotNet's own message and would otherwise be pre-empted here.
        }
    }
}
