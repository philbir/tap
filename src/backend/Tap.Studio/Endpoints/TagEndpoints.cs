using Tap.Studio.Contracts;
using Tap.Workspace.Model;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>GET /api/tags</c> — enumerates every tag applied to a collection, request, api,
/// or auth, one row per (tag, entity) pair. The UI client-side groups by tag for the
/// Tags top-level view.
///
/// <c>GET /api/tags/dictionary</c> — returns the union of the workspace's curated tag
/// list (declared in <c>workspace.tap</c> <c>tags:</c>) and every tag currently in use on any
/// entity. This is the autocomplete source for every <c>TagsInput</c> across the editors
/// and the picker source for the Tags-view filter.
/// </summary>
public static class TagEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tags/dictionary", (WorkspaceService svc) =>
        {
            var ws = svc.Current;
            var bag = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Curated list from workspace.tap
            if (ws.Manifest is { } manifest)
            {
                foreach (var t in manifest.Tags)
                    if (!string.IsNullOrWhiteSpace(t)) bag.Add(t);
            }

            // 2) Tags currently in use on any entity
            void Collect(IEnumerable<WorkspaceFile> files)
            {
                foreach (var f in files)
                    foreach (var t in f.Tags)
                        if (!string.IsNullOrWhiteSpace(t)) bag.Add(t);
            }
            Collect(ws.Collections);
            Collect(ws.Requests);
            Collect(ws.Auths);

            return Results.Ok((IReadOnlyList<string>)[.. bag]);
        });

        app.MapGet("/api/tags", (WorkspaceService svc) =>
        {
            var ws = svc.Current;
            var items = new List<TaggedItemDto>();

            void EmitTagsFor(string kind, WorkspaceFile f)
            {
                foreach (var tag in f.Tags)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    items.Add(new TaggedItemDto(tag, kind, f.RelativePath,
                        f.Name ?? Path.GetFileNameWithoutExtension(f.RelativePath)));
                }
            }

            foreach (var c in ws.Collections) EmitTagsFor("collection", c);
            foreach (var r in ws.Requests) EmitTagsFor("request", r);
            foreach (var au in ws.Auths) EmitTagsFor("auth", au);

            items.Sort((a, b) =>
            {
                var t = string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase);
                return t != 0 ? t : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return Results.Ok((IReadOnlyList<TaggedItemDto>)items);
        });
    }
}
