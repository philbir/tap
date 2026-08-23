using System.Text.RegularExpressions;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Workspace.Migration;

/// <summary>One ref this migration rewrites, for the dry-run report.</summary>
public sealed record RefRewrite(int Line, string Key, string From, string To);

/// <summary>What happens to one file: where it moves to, and which of its refs change.</summary>
public sealed record MigrationFileChange(
    string FromRelativePath,
    string ToRelativePath,
    string? RewrittenContent,
    IReadOnlyList<RefRewrite> Refs)
{
    public bool IsRename => !string.Equals(FromRelativePath, ToRelativePath, StringComparison.Ordinal);
}

/// <summary>
/// The full migration, computed before anything touches disk. <see cref="Blockers"/> being
/// non-empty means the plan must not be executed at all.
/// </summary>
public sealed record MigrationPlan(
    IReadOnlyList<MigrationFileChange> Changes,
    IReadOnlyList<string> Blockers)
{
    public bool IsNoOp => Changes.Count == 0;
    public int RenameCount => Changes.Count(c => c.IsRename);
    public int RefRewriteCount => Changes.Sum(c => c.Refs.Count);
}

/// <summary>
/// Plans the 0.7.0 extension migration: every legacy <c>.md</c> workspace file renamed to its
/// <c>.tap</c> equivalent, and every cross-file reference rewritten to match.
///
/// <para>The rename alone would break the workspace. A <see cref="WorkspaceRef"/> is a literal
/// relative path string carrying an extension (<c>auth: ../../auth/admin.auth.md</c>), so
/// renaming the target leaves a dangling ref. Both halves have to land together, which is why
/// this is a command and not a shell one-liner.</para>
///
/// <para>Refs are rewritten <em>textually</em>, inside the frontmatter block only. Round-tripping
/// through the parser and re-emitting would reformat files Tap did not author — reordering keys,
/// dropping comments, normalizing quotes — turning a mechanical rename into an unreviewable diff.
/// Replacing just the filename segment of the matched line keeps every untouched byte, and keeps
/// each ref's authored style (<c>./x</c>, <c>../x</c>, quoted or bare) intact.</para>
/// </summary>
public static class WorkspaceMigrator
{
    /// <summary>
    /// Frontmatter keys whose value is a <see cref="WorkspaceRef"/>. Derived from the
    /// <c>YamlExt.Ref(...)</c> call sites — request <c>auth:</c>, collection and env
    /// <c>defaultAuth:</c>, manifest <c>defaultEnv:</c>, flow step <c>request:</c>, and
    /// test entry <c>request:</c>/<c>flow:</c>. Adding a ref-valued key to the parser without
    /// adding it here would leave that ref dangling after a migration.
    /// </summary>
    public static readonly string[] RefKeys = ["auth", "defaultAuth", "defaultEnv", "request", "flow"];

    /// <summary>
    /// Matches a ref line in frontmatter. The optional <c>- </c> covers flow steps and test
    /// entries, which are list items; the leading indentation covers nested mappings.
    /// </summary>
    private static readonly Regex RefLine = new(
        @"^(?<prefix>\s*(?:-\s+)?)(?<key>" + string.Join('|', RefKeys) + @"):(?<gap>[ \t]*)(?<value>\S.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Computes the migration for <paramref name="workspace"/>. <paramref name="readSource"/>
    /// supplies each file's raw text by workspace-relative path.
    /// </summary>
    public static MigrationPlan Plan(LoadedWorkspace workspace, Func<string, string> readSource)
    {
        var blockers = new List<string>();

        // Migrating a workspace that doesn't parse would bake broken refs into the renamed files
        // and make the result far harder to reason about than the original problem.
        foreach (var error in workspace.Errors.Where(e => e.Severity == WorkspaceErrorSeverity.Error))
            blockers.Add($"{error.Code} {error.RelativePath ?? "(workspace)"}: {error.Message}");

        // from → to for every file that changes name. Canonical files stay put but may still
        // need their refs rewritten, so they are not in this map.
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in workspace.Files)
        {
            var fileName = Path.GetFileName(file.RelativePath);
            if (!KindResolver.IsLegacyFileName(fileName)) continue;

            var target = ReplaceFileName(file.RelativePath, KindResolver.ToCanonicalFileName(fileName));
            renames[file.RelativePath] = target;
        }

        // Two legacy files can only collide if they differ solely by extension, which the loader
        // already rejects — but a rename onto an unrelated existing path must never be silent.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in renames)
        {
            if (claimed.TryGetValue(to, out var other))
                blockers.Add($"'{from}' and '{other}' would both become '{to}'.");
            else
                claimed[to] = from;

            if (workspace.FindByPath(to) is not null)
                blockers.Add($"'{from}' cannot become '{to}' — that file already exists.");
        }

        var changes = new List<MigrationFileChange>();
        foreach (var file in workspace.Files)
        {
            var to = renames.GetValueOrDefault(file.RelativePath, file.RelativePath);

            string source;
            try
            {
                source = readSource(file.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                blockers.Add($"Could not read '{file.RelativePath}': {ex.Message}");
                continue;
            }

            var declaringDir = DirectoryOf(file.RelativePath);
            var (rewritten, refs) = RewriteRefs(source, declaringDir, renames);

            if (to == file.RelativePath && refs.Count == 0) continue; // nothing to do for this file
            changes.Add(new MigrationFileChange(file.RelativePath, to, refs.Count == 0 ? null : rewritten, refs));
        }

        return new MigrationPlan(changes, blockers);
    }

    /// <summary>
    /// Rewrites every ref in <paramref name="source"/>'s frontmatter that points at a renamed
    /// file. Returns the new text and the list of changes made.
    /// </summary>
    public static (string Text, IReadOnlyList<RefRewrite> Refs) RewriteRefs(
        string source,
        string declaringDirectory,
        IReadOnlyDictionary<string, string> renames)
    {
        var refs = new List<RefRewrite>();
        // Preserve the file's own line endings: splitting on \n and rejoining on \n would
        // silently convert a CRLF workspace, turning a rename into a whole-file diff.
        var lines = source.Split('\n');
        var end = FrontmatterEnd(lines);
        if (end < 0) return (source, refs);

        for (var i = 1; i < end; i++)
        {
            var raw = lines[i];
            var carriageReturn = raw.EndsWith('\r');
            var line = carriageReturn ? raw[..^1] : raw;

            var m = RefLine.Match(line);
            if (!m.Success) continue;

            var (value, quote, trailer) = SplitScalar(m.Groups["value"].Value);
            if (value.Length == 0 || value.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                continue; // id: refs survive a rename untouched — that is the point of them.

            var resolved = LoadedWorkspace.NormalizeRelative(
                declaringDirectory.Length == 0 ? value : $"{declaringDirectory}/{value}");
            if (!renames.TryGetValue(resolved, out var renamedTo)) continue;

            var newValue = ReplaceFileName(value, Path.GetFileName(renamedTo));
            refs.Add(new RefRewrite(i + 1, m.Groups["key"].Value, value, newValue));

            var rebuilt = m.Groups["prefix"].Value + m.Groups["key"].Value + ":" + m.Groups["gap"].Value
                + quote + newValue + quote + trailer;
            lines[i] = carriageReturn ? rebuilt + "\r" : rebuilt;
        }

        return (refs.Count == 0 ? source : string.Join('\n', lines), refs);
    }

    /// <summary>Index of the closing <c>---</c>, or -1 when the file has no frontmatter block.</summary>
    private static int FrontmatterEnd(string[] lines)
    {
        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") return i;
        }
        return -1;
    }

    /// <summary>
    /// Splits a YAML scalar into its value, the quote character wrapping it (empty if bare), and
    /// everything after it. A bare scalar ends at <c> #</c>; inside quotes a <c>#</c> is data.
    ///
    /// <para>Whitespace between the value and a trailing comment goes into the trailer rather
    /// than being trimmed away, so re-joining reproduces the line byte-for-byte apart from the
    /// value itself.</para>
    /// </summary>
    private static (string Value, string Quote, string Trailer) SplitScalar(string raw)
    {
        if (raw.Length > 0 && raw[0] is '"' or '\'')
        {
            var quote = raw[0];
            var close = raw.IndexOf(quote, 1);
            if (close > 0)
                return (raw[1..close], quote.ToString(), raw[(close + 1)..]);
        }

        var comment = raw.IndexOf(" #", StringComparison.Ordinal);
        var beforeComment = comment >= 0 ? raw[..comment] : raw;
        var value = beforeComment.TrimEnd();
        var trailer = beforeComment[value.Length..] + (comment >= 0 ? raw[comment..] : string.Empty);
        return (value, string.Empty, trailer);
    }

    /// <summary>Swaps the last segment of a forward-slash path, keeping every prefix byte —
    /// which is what preserves each ref's authored <c>./</c> or <c>../</c> style.</summary>
    private static string ReplaceFileName(string path, string fileName)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? fileName : string.Concat(path.AsSpan(0, cut + 1), fileName);
    }

    private static string DirectoryOf(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut < 0 ? string.Empty : relativePath[..cut];
    }
}
