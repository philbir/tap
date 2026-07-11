using Tap.Studio.Contracts;
using Tap.Workspace.Model;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/files/*</c> — sideband store for binary uploads referenced from
/// <c>.req.md</c> bodies via the <c>&lt; ./.files/name</c> marker. The Binary editor
/// posts a file here, the server writes it into a <c>.files/</c> directory next to
/// the owning request, and the response carries the ref string that the editor drops
/// into the request body. The executor (see <see cref="WorkspaceService"/>) resolves
/// the ref at send time and ships actual bytes via <c>ByteArrayContent</c>.
///
/// <para>Path safety: the request path goes through <see cref="WorkspacePathResolver"/>
/// before we touch the disk, and filenames are stripped to a basename so a payload
/// like <c>../../etc/passwd</c> can't tunnel out via the form field.</para>
/// </summary>
public static class FileEndpoints
{
    public const string FilesDirectoryName = ".files";
    private const long MaxFileSize = 50L * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/files/upload", async (HttpRequest req, WorkspaceService svc) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest("multipart/form-data required.");
            var form = await req.ReadFormAsync().ConfigureAwait(false);
            var requestPath = form["requestPath"].ToString();
            var file = form.Files["file"];
            if (string.IsNullOrWhiteSpace(requestPath)) return Results.BadRequest("requestPath form field required.");
            if (file is null || file.Length == 0) return Results.BadRequest("file form field required.");
            if (file.Length > MaxFileSize) return Results.BadRequest($"File too large (>{MaxFileSize / 1024 / 1024} MB).");

            // Anchor the ref against the request's directory so the same .files/ subdir is
            // reused by every request in that folder. Cross-request reuse is intentional —
            // operators often share a fixture file across a handful of requests.
            if (svc.Current.FindByPath(requestPath) is not RequestFile)
                return Results.NotFound($"Request '{requestPath}' not in workspace.");

            var requestDir = Path.GetDirectoryName(requestPath) ?? string.Empty;
            var safeName = SanitizeFilename(file.FileName);
            if (string.IsNullOrEmpty(safeName)) return Results.BadRequest("Invalid filename.");

            var relInside = string.IsNullOrEmpty(requestDir)
                ? $"{FilesDirectoryName}/{safeName}"
                : $"{requestDir}/{FilesDirectoryName}/{safeName}";

            if (!WorkspacePathResolver.TryResolve(svc, relInside, out var fullPath, out var err))
                return Results.BadRequest(err);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using (var stream = File.Create(fullPath))
            {
                await file.CopyToAsync(stream).ConfigureAwait(false);
            }

            // The ref is request-dir-relative (matches REST Client convention). The
            // executor strips a leading "./" before resolving, so this form is the one
            // we want to embed in the markdown body.
            var refText = $"< ./{FilesDirectoryName}/{safeName}";
            return Results.Ok(new FileUploadResponseDto(
                Ref: refText,
                RelativePath: relInside,
                Name: safeName,
                Size: file.Length,
                ContentType: string.IsNullOrEmpty(file.ContentType) ? null : file.ContentType));
        }).DisableAntiforgery();
    }

    /// <summary>Strip path separators + leading dots so an attacker can't smuggle a
    /// traversal segment via the form field. Browsers strip <c>C:\path\…</c>-style
    /// prefixes from <c>file.name</c> already; this is defense in depth.</summary>
    private static string SanitizeFilename(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var name = Path.GetFileName(input.Replace('\\', '/'));
        // Belt-and-braces: also nuke any residual NUL or control chars so the OS layer
        // never sees one. Whitespace-only filenames collapse to empty (rejected above).
        var clean = new string(name.Where(c => c >= 32 && c != 127 && c != ':' && c != '*' && c != '?' && c != '"' && c != '<' && c != '>' && c != '|').ToArray());
        clean = clean.TrimStart('.').Trim();
        return clean;
    }
}
