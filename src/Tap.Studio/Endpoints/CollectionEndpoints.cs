using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/collections/...</c> — read + write the structured spec for a
/// <c>_collection.md</c>. Collections live at <c>collections/&lt;slug&gt;/_collection.md</c>;
/// the client passes the slug as the path segment (e.g. <c>demo</c>) and the server appends
/// the rest. A collection directory without <c>_collection.md</c> still returns a
/// <see cref="CollectionDetailDto"/> with <c>Exists=false</c> so the editor can render a
/// create-on-save form.
/// </summary>
public static class CollectionEndpoints
{
    private const string CollectionsRoot = "collections";

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/collections");

        g.MapGet("/", (WorkspaceService svc) =>
        {
            var ws = svc.Current;
            var items = new List<CollectionSummaryDto>();
            var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in ws.Collections)
            {
                var slug = SlugFromCollectionFile(c.RelativePath);
                if (slug is null) continue;
                items.Add(new CollectionSummaryDto(
                    slug,
                    c.Name ?? slug,
                    c.Id,
                    Exists: true,
                    BaseUrl: c.BaseUrl,
                    StageNames: c.Stages.Select(s => s.Name).ToArray(),
                    DefaultStage: c.DefaultStage));
                seenSlugs.Add(slug);
            }

            var collectionsRoot = Path.Combine(ws.TapDirectory, CollectionsRoot);
            if (Directory.Exists(collectionsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(collectionsRoot))
                {
                    var slug = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(slug) || seenSlugs.Contains(slug)) continue;
                    items.Add(new CollectionSummaryDto(slug, slug, null, false, string.Empty, Array.Empty<string>(), null));
                }
            }

            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return Results.Ok((IReadOnlyList<CollectionSummaryDto>)items);
        });

        g.MapGet("/{slug}", (string slug, WorkspaceService svc) =>
        {
            if (!IsValidSlug(slug)) return Results.NotFound();
            var ws = svc.Current;
            var path = $"{CollectionsRoot}/{slug}/_collection.md";
            if (ws.FindByPath(path) is CollectionFile c)
            {
                return Results.Ok(new CollectionDetailDto(
                    Slug: slug,
                    Name: c.Name ?? slug,
                    Id: c.Id,
                    Exists: true,
                    BaseUrl: c.BaseUrl,
                    DefaultAuth: c.DefaultAuth?.RelativePath,
                    DefaultHeaders: c.DefaultHeaders,
                    Vars: c.Vars,
                    Tags: c.Tags,
                    Body: c.Body,
                    Source: svc.ReadSource(path),
                    Stages: c.Stages.Select(s => new CollectionStageDto(
                        Name: s.Name,
                        BaseUrl: s.BaseUrl,
                        DefaultAuth: s.DefaultAuth?.RelativePath,
                        Vars: s.Vars)).ToArray(),
                    DefaultStage: c.DefaultStage));
            }

            var dirAbs = Path.Combine(ws.TapDirectory, CollectionsRoot, slug);
            if (!Directory.Exists(dirAbs)) return Results.NotFound();
            return Results.Ok(new CollectionDetailDto(
                Slug: slug,
                Name: slug,
                Id: null,
                Exists: false,
                BaseUrl: string.Empty,
                DefaultAuth: null,
                DefaultHeaders: new Dictionary<string, string>(),
                Vars: new Dictionary<string, VarSpec>(),
                Tags: Array.Empty<string>(),
                Body: string.Empty,
                Source: string.Empty,
                Stages: Array.Empty<CollectionStageDto>(),
                DefaultStage: null));
        });

        g.MapPut("/spec", (CollectionSpecDto spec, WorkspaceService svc) =>
        {
            if (!IsValidSlug(spec.Slug))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-slug",
                    "Collection slug must be alphanumeric, dashes, or underscores.", null, null));

            if (!WorkspacePathResolver.TryResolve(svc, $"{CollectionsRoot}/{spec.Slug}",
                    out var dirAbs, out var dirErr))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-slug", dirErr, null, null));
            Directory.CreateDirectory(dirAbs);

            var content = CollectionSpecEmitter.ToFileSource(spec);
            var relPath = $"{CollectionsRoot}/{spec.Slug}/_collection.md";
            try { svc.Save(relPath, content); return Results.NoContent(); }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
        });

        g.MapDelete("/{slug}", (string slug, WorkspaceService svc) =>
        {
            if (!IsValidSlug(slug))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-slug",
                    "Collection slug must be alphanumeric, dashes, or underscores.", null, null));

            if (!WorkspacePathResolver.TryResolve(svc, $"{CollectionsRoot}/{slug}",
                    out var dirAbs, out var dirErr))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-slug", dirErr, null, null));
            if (!Directory.Exists(dirAbs))
                return Results.BadRequest(new WorkspaceErrorDto("not-found",
                    "Collection does not exist.", null, null));

            Directory.Delete(dirAbs, recursive: true);
            return Results.NoContent();
        });
    }

    private static string? SlugFromCollectionFile(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Length != 3) return null;
        if (!string.Equals(parts[0], CollectionsRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(parts[2], "_collection.md", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[1];
    }

    private static bool IsValidSlug(string slug)
        => !string.IsNullOrWhiteSpace(slug)
        && slug.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');
}
