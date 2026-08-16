using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Maps filename suffix → <see cref="WorkspaceKind"/> per spec §2. The filename suffix is
/// canonical; the <c>kind:</c> frontmatter field must match or the parser raises
/// <see cref="WorkspaceErrorCode.E_KIND_MISMATCH"/>.
///
/// <para>Two suffix families resolve to the same kinds: the canonical <c>.tap</c> family, and the
/// legacy <c>.md</c> family it replaced in 0.7.0. Both are read; only <c>.tap</c> is ever written.
/// Nothing about the file's <em>content</em> differs between them — same frontmatter, same
/// markdown body, same fenced blocks — so the two families are interchangeable on load. The
/// legacy family is scheduled for removal in 0.8.0, so every legacy hit carries
/// <see cref="Match.IsLegacy"/> and the loader turns that into a warning.</para>
/// </summary>
public static class KindResolver
{
    /// <summary>Canonical extension for every Tap workspace file since 0.7.0.</summary>
    public const string CanonicalExtension = ".tap";

    /// <summary>The extension Tap files carried before 0.7.0. Still read; never written.</summary>
    public const string LegacyExtension = ".md";

    /// <summary>Canonical manifest filename. <c>workspace.tap</c> rather than the stuttering
    /// <c>tap.tap</c>.</summary>
    public const string ManifestFileName = "workspace" + CanonicalExtension;

    public const string LegacyManifestFileName = "tap" + LegacyExtension;

    public const string CollectionFileName = "_collection" + CanonicalExtension;

    public const string LegacyCollectionFileName = "_collection" + LegacyExtension;

    /// <summary>
    /// The kind-bearing infix of every suffixed family member: <c>orders.req.tap</c> → <c>req</c>.
    /// The two whole-name kinds (manifest, collection) are matched exactly instead, so that
    /// <c>my_collection.tap</c> and <c>myworkspace.tap</c> stay ordinary names.
    /// </summary>
    private static readonly (string Infix, WorkspaceKind Kind)[] Infixes =
    [
        ("req", WorkspaceKind.Request),
        ("auth", WorkspaceKind.Auth),
        ("env", WorkspaceKind.Env),
        ("flow", WorkspaceKind.Flow),
        ("test", WorkspaceKind.Test),
    ];

    private static readonly (string Name, WorkspaceKind Kind, bool IsLegacy)[] ExactNames =
    [
        (ManifestFileName, WorkspaceKind.Workspace, false),
        (LegacyManifestFileName, WorkspaceKind.Workspace, true),
        (CollectionFileName, WorkspaceKind.Collection, false),
        (LegacyCollectionFileName, WorkspaceKind.Collection, true),
    ];

    /// <summary>Both families' suffixes, precomputed — <see cref="Resolve"/> runs once per file in
    /// a workspace scan, so it must not allocate.</summary>
    private static readonly (string Suffix, WorkspaceKind Kind, bool IsLegacy)[] Suffixes =
        Infixes
            .SelectMany(x => new[]
            {
                ($".{x.Infix}{CanonicalExtension}", x.Kind, false),
                ($".{x.Infix}{LegacyExtension}", x.Kind, true),
            })
            .ToArray();

    /// <summary>
    /// The portable request format (Visual Studio, REST Client, JetBrains, httpyac, Kulala).
    /// Unlike the two families above this is not a Tap-authored format — a <c>.http</c> file has
    /// no frontmatter, holds several requests, and is never rewritten by Tap.
    /// </summary>
    public const string HttpExtension = ".http";

    /// <summary>A recognized workspace filename: which kind it is, whether it spells itself the
    /// pre-0.7.0 way, and whether it is a portable <c>.http</c> file rather than a Tap file.</summary>
    public readonly record struct Match(WorkspaceKind Kind, bool IsLegacy, bool IsHttpFile = false);

    /// <summary>True when the name is a portable <c>.http</c> file.</summary>
    public static bool IsHttpFileName(string fileName)
        => fileName.EndsWith(HttpExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a filename against every recognized family.</summary>
    public static Match? Resolve(string fileName)
    {
        if (IsHttpFileName(fileName))
            return new Match(WorkspaceKind.Request, IsLegacy: false, IsHttpFile: true);

        foreach (var (name, kind, isLegacy) in ExactNames)
        {
            if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return new Match(kind, isLegacy);
        }

        foreach (var (suffix, kind, isLegacy) in Suffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new Match(kind, isLegacy);
        }

        return null;
    }

    /// <summary>Kind-only resolution, for the many callers that don't care which family a file
    /// came from.</summary>
    public static WorkspaceKind? FromFileName(string fileName) => Resolve(fileName)?.Kind;

    /// <summary>True when the name is a recognized workspace file spelled the legacy way.</summary>
    public static bool IsLegacyFileName(string fileName) => Resolve(fileName) is { IsLegacy: true };

    /// <summary>
    /// The canonical filename this one migrates to: <c>orders.req.md</c> → <c>orders.req.tap</c>,
    /// <c>tap.md</c> → <c>workspace.tap</c>. Returns the input unchanged when it is already
    /// canonical or is not a workspace file at all, which is what makes migration idempotent.
    /// </summary>
    public static string ToCanonicalFileName(string fileName)
    {
        if (Resolve(fileName) is not { IsLegacy: true } match)
            return fileName;

        return match.Kind switch
        {
            WorkspaceKind.Workspace => ManifestFileName,
            WorkspaceKind.Collection => CollectionFileName,
            // Suffixed families keep their stem and swap only the trailing extension.
            _ => string.Concat(fileName.AsSpan(0, fileName.Length - LegacyExtension.Length), CanonicalExtension),
        };
    }

    /// <summary>
    /// The legacy spelling of a canonical filename, or null when the name isn't a canonical
    /// workspace file. The inverse of <see cref="ToCanonicalFileName"/>, used to find the file a
    /// save should overwrite in a workspace that hasn't migrated yet.
    /// </summary>
    public static string? ToLegacyFileName(string fileName)
    {
        if (Resolve(fileName) is not { IsLegacy: false } match)
            return null;

        return match.Kind switch
        {
            WorkspaceKind.Workspace => LegacyManifestFileName,
            WorkspaceKind.Collection => LegacyCollectionFileName,
            _ => string.Concat(fileName.AsSpan(0, fileName.Length - CanonicalExtension.Length), LegacyExtension),
        };
    }

    /// <summary>
    /// The canonical on-disk filename for a new file of <paramref name="kind"/>. The two
    /// whole-name kinds ignore <paramref name="slug"/> — their filename is fixed and their
    /// identity comes from the folder they sit in.
    /// </summary>
    public static string FileNameFor(WorkspaceKind kind, string slug) => kind switch
    {
        WorkspaceKind.Workspace => ManifestFileName,
        WorkspaceKind.Collection => CollectionFileName,
        _ => slug + SuffixFor(kind),
    };

    /// <summary>The canonical suffix for a kind that has one (everything but manifest and
    /// collection, whose names are whole).</summary>
    public static string SuffixFor(WorkspaceKind kind)
    {
        foreach (var (infix, k) in Infixes)
        {
            if (k == kind) return $".{infix}{CanonicalExtension}";
        }

        throw new ArgumentOutOfRangeException(
            nameof(kind), kind, $"{kind} files are named exactly, not by suffix — use {nameof(FileNameFor)}.");
    }

    /// <summary>Human-readable list of the canonical names, for error messages. Generated from
    /// the table so it cannot drift the way a hardcoded list did.</summary>
    public static string KnownNamesDescription { get; } = string.Join(
        ", ",
        Infixes.Select(x => $"*.{x.Infix}{CanonicalExtension}")
            .Concat([CollectionFileName, ManifestFileName]));

    public static string FrontmatterValue(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Request => "request",
        WorkspaceKind.Auth => "auth",
        WorkspaceKind.Env => "env",
        WorkspaceKind.Collection => "collection",
        WorkspaceKind.Workspace => "workspace",
        WorkspaceKind.Flow => "flow",
        WorkspaceKind.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static WorkspaceKind? ParseFrontmatterValue(string value) => value switch
    {
        "request" => WorkspaceKind.Request,
        "auth" => WorkspaceKind.Auth,
        "env" => WorkspaceKind.Env,
        "collection" => WorkspaceKind.Collection,
        "workspace" => WorkspaceKind.Workspace,
        "flow" => WorkspaceKind.Flow,
        "test" => WorkspaceKind.Test,
        _ => null,
    };
}
