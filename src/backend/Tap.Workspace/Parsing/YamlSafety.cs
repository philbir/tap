using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Screens untrusted YAML for the two shapes that turn parsing into a denial of service, before
/// the text reaches a representation model.
///
/// <para><b>Anchors and aliases</b> are rejected outright. A mapping whose <i>key</i> aliases a
/// deeply nested anchor forces YamlDotNet to hash and compare the fully expanded graph while
/// building <c>YamlMappingNode.Children</c>. Measured against the pinned YamlDotNet 18.1.0 that is
/// roughly 9x per nesting level: 425 bytes took 1.6 s, 520 bytes took 105 s.</para>
///
/// <para><b>Excessive nesting</b> is rejected because the representation model is built by
/// recursive descent — deep enough input overflows the stack, which no <c>catch</c> can save.</para>
///
/// <para>The scan runs on the low-level event stream, which is linear in the input and never
/// expands an alias — so the guard cannot itself be turned into the bomb it looks for.</para>
///
/// <para>Callers get a reason string rather than an exception so each can report the failure in
/// its own vocabulary: the workspace loader speaks of "frontmatter", the OpenAPI importer of
/// "the document".</para>
/// </summary>
public static class YamlSafety
{
    /// <summary>Why the text was rejected, and the 1-based line it happened on.</summary>
    public sealed record Rejection(YamlRejectionKind Kind, int Line);

    /// <summary>
    /// Returns null when the text is safe to load. <paramref name="maxDepth"/> bounds nesting.
    ///
    /// <para>Malformed YAML is <i>not</i> a rejection — it returns null so the real load reports
    /// the syntax error with its own message rather than being pre-empted by a vaguer one here.</para>
    /// </summary>
    public static Rejection? Screen(string yamlText, int maxDepth)
    {
        var parser = new Parser(new StringReader(yamlText));
        var depth = 0;
        try
        {
            while (parser.MoveNext())
            {
                var current = parser.Current;
                if (current is null) continue;

                var line = (int)current.Start.Line;

                if (current is AnchorAlias)
                    return new Rejection(YamlRejectionKind.Alias, line);

                if (current is NodeEvent node && !node.Anchor.IsEmpty)
                    return new Rejection(YamlRejectionKind.Anchor, line);

                depth += current.NestingIncrease;
                if (depth > maxDepth)
                    return new Rejection(YamlRejectionKind.TooDeep, line);
            }
        }
        catch (YamlException)
        {
            // See the summary: syntax errors belong to the real load.
        }

        return null;
    }
}

public enum YamlRejectionKind
{
    /// <summary>A <c>*alias</c> reference.</summary>
    Alias,

    /// <summary>An <c>&amp;anchor</c> declaration.</summary>
    Anchor,

    /// <summary>Nesting beyond the caller's limit.</summary>
    TooDeep,
}
