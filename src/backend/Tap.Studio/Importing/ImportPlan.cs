using System.Text;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Studio.Importing;

/// <summary>One file an importer wants written, as a workspace-relative path and its content.</summary>
public sealed record ImportFile(string RelativePath, string Content);

/// <summary>
/// The output of any importer: a set of (relative path, content) pairs plus whatever was lossy
/// about the conversion.
///
/// <para><b>Importers are pure planners.</b> Nothing here touches the filesystem — the calling
/// endpoint writes every file through <c>WorkspaceService.Save</c> so an imported file passes
/// exactly the same path-safety and parse-validation gates as a hand edit. Keeping the plan a
/// value also means a preview and the real import run the identical code path.</para>
/// </summary>
public sealed record ImportPlan(
    string Slug,
    string CollectionPath,
    string? AuthPath,
    IReadOnlyList<ImportFile> Files,
    IReadOnlyList<string> Warnings)
{
    private readonly int? _requestCount;

    /// <summary>
    /// How many requests the plan creates. Defaults to counting <c>*.req.tap</c> files, which is
    /// right for one-request-per-file importers; importers that pack N requests into a single
    /// <c>.http</c> file set it explicitly, since counting files would report the file count.
    /// </summary>
    public int RequestCount
    {
        get => _requestCount ?? Files.Count(f => f.RelativePath.EndsWith(
            KindResolver.SuffixFor(WorkspaceKind.Request), StringComparison.OrdinalIgnoreCase));
        init => _requestCount = value;
    }

    public int FolderCount { get; init; }
}

/// <summary>
/// Slug rules shared by every importer, so a collection imported from Postman and one imported
/// from OpenAPI produce the same filenames for the same names.
/// </summary>
public static class ImportSlug
{
    /// <summary>Longest slug we emit. Keeps generated paths well clear of filesystem limits
    /// once the directory prefix and <c>.req.tap</c> suffix are added.</summary>
    private const int MaxLength = 60;

    /// <summary>
    /// Lowercases, collapses runs of separators to a single dash, drops everything that isn't a
    /// letter or digit, and trims to <see cref="MaxLength"/>. Returns empty when nothing usable
    /// survives — callers decide the fallback, because a good default differs by importer.
    /// </summary>
    public static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        bool lastDash = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (ch is '_' or '-')
            {
                if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\' or '.' or ':')
            {
                if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
        }
        var s = sb.ToString().Trim('-');
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd('-') : s;
    }

    /// <summary>
    /// <c>petId</c> → <c>pet Id</c>, so slugification produces <c>pet-id</c> instead of
    /// <c>petid</c>.
    ///
    /// <para>Shared because every importer meets the same problem: an <c>operationId</c>, a WSDL
    /// operation name, and a security-scheme key are all camelCase by convention, and slugifying
    /// one directly collapses it into an unreadable run of letters.</para>
    /// </summary>
    public static string SplitCamelCase(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1])) sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Slugifies and disambiguates against names already taken in the same directory, appending
    /// <c>-2</c>, <c>-3</c>… <paramref name="siblings"/> is mutated to record the result, so
    /// callers keep one set per directory.
    /// </summary>
    public static string UniqueSlug(string name, HashSet<string> siblings, string fallback = "item")
    {
        var baseSlug = Slugify(name);
        if (baseSlug.Length == 0) baseSlug = fallback;
        var slug = baseSlug;
        var i = 2;
        while (!siblings.Add(slug))
        {
            slug = $"{baseSlug}-{i++}";
        }
        return slug;
    }
}
