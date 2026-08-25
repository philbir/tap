using System.Text;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Declares one variable into a workspace file's <c>vars:</c> map, leaving the rest of the
/// file alone.
///
/// <para>This is deliberately not a spec round-trip. The spec DTOs flatten <c>vars</c> to
/// <c>map&lt;string,string&gt;</c> plus a <c>secrets</c> list, so re-emitting a whole file to add
/// one entry would quietly drop every <c>description</c> / <c>required</c> / <c>example</c> the
/// other entries carry. And the caller is a field in some <i>other</i> editor — a header value in
/// a request being converted to a workspace-scoped variable. It has no business rewriting a file
/// the user never opened, so "add the entry, touch nothing else" is the contract.
/// </para>
///
/// <para><b>Why text and not nodes.</b> The obvious implementation patches the parsed frontmatter
/// and re-serializes it. That is correct about values and wrong about the file: YamlDotNet's
/// representation model carries no comments, so re-serializing silently deletes every one of
/// them. In a hand-authored, Git-committed file that is real damage, and it arrives from an
/// action the user framed as editing a different file entirely. So the entry is spliced into the
/// frontmatter's own lines, and everything the splice does not touch survives byte for byte.</para>
///
/// <para><b>Why that is safe.</b> A text splice can mis-land in ways a node patch cannot, so it is
/// never trusted: <see cref="Apply"/> computes the node-patched frontmatter as the reference
/// answer and accepts the splice only when re-parsing it yields exactly that. Anything the scan
/// doesn't read — flow-style <c>vars: {…}</c>, an unfamiliar indentation shape — fails the check
/// and falls back to the node patch, which loses comments but is always right. Correctness is the
/// floor; comment preservation is the improvement on top of it.</para>
/// </summary>
public static class VarDeclarationWriter
{
    /// <summary>
    /// Returns <paramref name="content"/> with <paramref name="name"/> declared under
    /// <c>vars:</c>. An existing entry of the same name is replaced. A secret entry is written in
    /// the <c>{ default: …, secret: true }</c> form and a plain one as a bare scalar — matching
    /// <see cref="SpecYaml.SetVarMap"/>, so a later save through the kind's own editor is a no-op
    /// in the diff.
    /// </summary>
    /// <exception cref="WorkspaceParseException">The file has no frontmatter, leaves it unclosed,
    /// or the YAML between the fences is malformed.</exception>
    public static string Apply(string content, string relativePath, string name, string value, bool secret)
    {
        var parts = WorkspaceFileText.Split(content, relativePath);
        var entry = SpecYaml.VarNode(value, secret);

        // The reference answer: what the frontmatter must mean once the entry is in. Computed
        // first because it doubles as the fallback and as the splice's acceptance test.
        SetVar(parts.Frontmatter, name, entry);
        var canonical = SpecYaml.ToFrontmatterOver(parts.Frontmatter, parts.Body);

        var spliced = TrySplice(content, name, entry);
        return spliced is not null && MeansTheSame(spliced, canonical, relativePath) ? spliced : canonical;
    }

    /// <summary>Sets one entry on a frontmatter mapping, creating <c>vars:</c> if absent.</summary>
    private static void SetVar(YamlMappingNode frontmatter, string name, YamlNode entry)
    {
        // `vars:` may be absent, or present but empty — the latter parses as a null scalar rather
        // than a mapping. Both mean "start one".
        var varsKey = new YamlScalarNode("vars");
        if (!frontmatter.Children.TryGetValue(varsKey, out var existing) || existing is not YamlMappingNode vars)
        {
            vars = new YamlMappingNode();
            frontmatter.Children.Remove(varsKey);
            // Appended rather than slotted into canonical position: reordering a file this editor
            // does not own would churn a diff the user did not ask for. The kind's own editor
            // canonicalizes the order on its next save.
            frontmatter.Children.Add(varsKey, vars);
        }

        var entryKey = new YamlScalarNode(name);
        vars.Children.Remove(entryKey);
        vars.Children.Add(entryKey, entry);
    }

    /// <summary>
    /// True when <paramref name="spliced"/> parses to the same frontmatter and body as
    /// <paramref name="canonical"/>. Both sides are re-serialized from their parsed form, which
    /// strips exactly the formatting the splice exists to preserve — so this compares meaning,
    /// not text.
    /// </summary>
    private static bool MeansTheSame(string spliced, string canonical, string relativePath)
    {
        try
        {
            var a = WorkspaceFileText.Split(spliced, relativePath);
            var b = WorkspaceFileText.Split(canonical, relativePath);
            return a.Body == b.Body
                && SpecYaml.ToFrontmatterOver(a.Frontmatter, string.Empty)
                   == SpecYaml.ToFrontmatterOver(b.Frontmatter, string.Empty);
        }
        catch (WorkspaceParseException)
        {
            return false;
        }
    }

    /// <summary>
    /// Splices the entry into the frontmatter's raw lines, or returns null when the file's shape
    /// isn't one this scan reads. Never validates its own work — <see cref="Apply"/> does that.
    /// </summary>
    private static string? TrySplice(string content, string name, YamlNode entry)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0 || Trim(lines[0]) != "---") return null;

        var close = Array.FindIndex(lines, 1, l => Trim(l) == "---");
        if (close < 0) return null;

        var fm = new List<string>(lines[1..close]);
        var cr = lines[0].EndsWith('\r') ? "\r" : string.Empty;
        var varsIdx = fm.FindIndex(l => Trim(l) == "vars:");

        // `vars: {a: 1}` and friends land here — the flow forms this scan doesn't read. Falling
        // back is exactly what returning null is for.
        if (varsIdx < 0 && fm.Any(l => Trim(l).StartsWith("vars:", StringComparison.Ordinal))) return null;

        if (varsIdx < 0)
        {
            fm.Add("vars:" + cr);
            fm.AddRange(Indent(Render(name, entry), "  ", cr));
            return Reassemble(lines, fm, close, cr);
        }

        // The block runs to the first line that starts a new top-level key. Blank lines and
        // anything indented belong to it — including trailing comments, which stay put.
        var blockEnd = varsIdx + 1;
        while (blockEnd < fm.Count && (IsBlank(fm[blockEnd]) || StartsIndented(fm[blockEnd]))) blockEnd++;

        var indent = ChildIndent(fm, varsIdx + 1, blockEnd) ?? "  ";
        var rendered = Indent(Render(name, entry), indent, cr);
        var keyIdx = FindKey(fm, varsIdx + 1, blockEnd, indent, name);

        if (keyIdx < 0)
        {
            // Append after the block's last line of substance, so a blank line separating `vars:`
            // from the next key stays where its author put it.
            var insertAt = blockEnd;
            while (insertAt > varsIdx + 1 && IsBlank(fm[insertAt - 1])) insertAt--;
            fm.InsertRange(insertAt, rendered);
        }
        else
        {
            // The entry is the key's line plus everything nested under it.
            var end = keyIdx + 1;
            while (end < blockEnd && (IsBlank(fm[end]) || IndentOf(fm[end]).Length > indent.Length)) end++;
            while (end > keyIdx + 1 && IsBlank(fm[end - 1])) end--;
            fm.RemoveRange(keyIdx, end - keyIdx);
            fm.InsertRange(keyIdx, rendered);
        }

        return Reassemble(lines, fm, close, cr);
    }

    /// <summary>The one entry as YAML lines, carrying no indentation of its own. Routed through
    /// the emitter so the spliced text is character-identical to what the node path produces,
    /// quoting included — a <c>{{provider:key}}</c> reference has to come out single-quoted or
    /// YAML reads it as a flow mapping.</summary>
    private static string[] Render(string name, YamlNode entry)
    {
        var map = new YamlMappingNode();
        map.Add(new YamlScalarNode(name), entry);
        return SpecYaml.ToFrontmatterOver(map, string.Empty)
            .Split('\n')
            .Where(l => Trim(l) != "---" && Trim(l).Length > 0)
            .ToArray();
    }

    private static List<string> Indent(string[] rendered, string indent, string cr)
        => rendered.Select(l => indent + l.TrimEnd('\r') + cr).ToList();

    /// <summary>The indentation this block's existing entries use, or null when it has none.</summary>
    private static string? ChildIndent(List<string> fm, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (IsBlank(fm[i])) continue;
            var indent = IndentOf(fm[i]);
            if (indent.Length > 0) return indent;
        }
        return null;
    }

    /// <summary>The line declaring <paramref name="name"/> at this block's own indent, or -1.
    /// A quoted key never matches: re-emitting it unquoted would change the file beyond the one
    /// entry, and the acceptance test would reject the result anyway.</summary>
    private static int FindKey(List<string> fm, int from, int to, string indent, string name)
    {
        for (var i = from; i < to; i++)
        {
            var line = Trim(fm[i]);
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (IndentOf(fm[i]) != indent) continue;
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon] == name) return i;
        }
        return -1;
    }

    private static string Reassemble(string[] lines, List<string> fm, int close, string cr)
    {
        var sb = new StringBuilder();
        sb.Append("---").Append(cr).Append('\n');
        foreach (var l in fm) sb.Append(l).Append('\n');
        sb.Append("---").Append(cr).Append('\n');
        // Everything past the closing fence, exactly as it was.
        for (var i = close + 1; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Trim(string line) => line.TrimEnd('\r').Trim();
    private static bool IsBlank(string line) => Trim(line).Length == 0;
    private static bool StartsIndented(string line) => line.Length > 0 && (line[0] == ' ' || line[0] == '\t');

    private static string IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }
}
