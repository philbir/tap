using Tap.Studio.Contracts;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/workspace*</c> endpoints — read-only views of the loaded workspace.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/workspace");

        g.MapGet("/", (WorkspaceService svc) =>
        {
            var ws = svc.Current;
            var info = new WorkspaceInfoDto(
                Name: ws.Manifest?.Name ?? Path.GetFileName(ws.RootDirectory) ?? "tap-workspace",
                Root: ws.RootDirectory,
                DefaultEnv: ws.Manifest?.DefaultEnv?.RelativePath,
                Providers: ws.Manifest?.VariableProviders.Select(p => p.Name).ToArray() ?? [],
                Errors: ws.Errors.Select(e => new WorkspaceErrorDto(e.Code, e.Message, e.RelativePath, e.Line)).ToArray());
            return Results.Ok(info);
        });

        g.MapGet("/tree", (WorkspaceService svc) =>
        {
            var ws = svc.Current;
            return Results.Ok(BuildTree(ws));
        });

        g.MapPost("/reload", (WorkspaceService _) =>
        {
            // Watcher already reloads automatically; this is a manual nudge for the UI.
            return Results.NoContent();
        });

        // Raw-source save (used by every editor's Source tab). WorkspaceService.Save
        // round-trips through FileParser, so invalid YAML / mismatched kind / etc are
        // rejected with a 400 + WorkspaceErrorDto before anything lands on disk.
        g.MapPut("/source", (SaveSourceDto body, WorkspaceService svc) =>
        {
            var path = (body.Path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(path))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-path", "Path is required.", null, null));
            try
            {
                svc.Save(path, body.Content ?? string.Empty);
                return Results.NoContent();
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto("invalid-kind", ex.Message, path, null));
            }
        });

        g.MapPost("/folders", (CreateFolderDto body, WorkspaceService svc) =>
        {
            if (!TryResolveWorkspacePath(svc, body.Path, out var full, out var err))
                return Results.BadRequest(new { code = "invalid-path", message = err });
            if (Directory.Exists(full)) return Results.BadRequest(new { code = "exists", message = "Folder already exists." });
            if (File.Exists(full)) return Results.BadRequest(new { code = "exists", message = "A file with that name already exists." });
            Directory.CreateDirectory(full);
            return Results.NoContent();
        });

        g.MapDelete("/folders", (string path, WorkspaceService svc) =>
        {
            if (!TryResolveWorkspacePath(svc, path, out var full, out var err))
                return Results.BadRequest(new { code = "invalid-path", message = err });
            if (!Directory.Exists(full)) return Results.BadRequest(new { code = "not-found", message = "Folder does not exist." });

            // Refuse to nuke the active workspace content root.
            var tapDirFull = Path.GetFullPath(svc.Current.TapDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), tapDirFull, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { code = "protected", message = "Cannot delete the workspace root." });

            Directory.Delete(full, recursive: true);
            return Results.NoContent();
        });

        // Delete a single workspace file (request / auth / env / collection meta).
        // The path is workspace-relative under `.tap/`. Refuses directories — callers
        // should hit DELETE /folders for those so they get the recursive semantics.
        g.MapDelete("/files", (string path, WorkspaceService svc) =>
        {
            if (!TryResolveWorkspacePath(svc, path, out var full, out var err))
                return Results.BadRequest(new { code = "invalid-path", message = err });
            if (Directory.Exists(full))
                return Results.BadRequest(new { code = "is-directory", message = "Use DELETE /folders for directories." });
            if (!File.Exists(full))
                return Results.BadRequest(new { code = "not-found", message = "File does not exist." });
            File.Delete(full);
            return Results.NoContent();
        });

        g.MapPost("/move", (MoveItemDto body, WorkspaceService svc) =>
        {
            if (!TryResolveWorkspacePath(svc, body.From, out var fromFull, out var err))
                return Results.BadRequest(new { code = "invalid-path", message = err });
            if (!TryResolveWorkspacePath(svc, body.To, out var toFull, out err))
                return Results.BadRequest(new { code = "invalid-path", message = err });

            var isDir = Directory.Exists(fromFull);
            if (!isDir && !File.Exists(fromFull))
                return Results.BadRequest(new { code = "not-found", message = "Source does not exist." });
            if (File.Exists(toFull) || Directory.Exists(toFull))
                return Results.BadRequest(new { code = "exists", message = "Destination already exists." });

            // Prevent moving a directory into itself or its descendants.
            if (isDir)
            {
                var fromWithSep = fromFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (toFull.StartsWith(fromWithSep, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { code = "circular", message = "Cannot move a folder into itself." });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
            if (isDir) Directory.Move(fromFull, toFull);
            else File.Move(fromFull, toFull);
            // The client refetches the moved file at its new path as soon as we return —
            // the watcher's debounced reload would lose that race and 404.
            svc.ReloadNow();
            return Results.NoContent();
        });

        // Workspace switcher — list/add/remove/activate known workspaces.
        var w = app.MapGroup("/api/workspaces");

        w.MapGet("/", (KnownWorkspaceStore store) =>
        {
            var active = store.ActivePath;
            var dtos = store.List()
                .Select(k => new KnownWorkspaceDto(
                    Path: k.Path,
                    Label: k.Label,
                    IsActive: string.Equals(k.Path, active, StringComparison.Ordinal),
                    Available: Directory.Exists(k.Path),
                    Git: BuildGitDto(k.Path, k.GitRoot)))
                .ToArray();
            return Results.Ok((IReadOnlyList<KnownWorkspaceDto>)dtos);
        });

        w.MapPost("/", (AddWorkspaceDto body, KnownWorkspaceStore store) =>
        {
            try
            {
                var added = store.Add(body.Path);
                return Results.Ok(new KnownWorkspaceDto(
                    added.Path, added.Label, IsActive: false, Available: true,
                    Git: BuildGitDto(added.Path, added.GitRoot)));
            }
            catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { code = "missing-folder", message = ex.Message }); }
        });

        w.MapPost("/activate", (ActivateWorkspaceDto body, WorkspaceService svc) =>
        {
            try { svc.SwitchTo(body.Path); return Results.NoContent(); }
            catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { code = "missing-folder", message = ex.Message }); }
        });

        w.MapDelete("/", (string path, KnownWorkspaceStore store) =>
        {
            try { store.Remove(path); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "active-workspace", message = ex.Message }); }
        });
    }

    private static IReadOnlyList<TreeNodeDto> BuildTree(LoadedWorkspace ws)
    {
        // Group files into a directory tree, mirroring the on-disk layout.
        var nodesByDir = new Dictionary<string, List<TreeNodeDto>>(StringComparer.OrdinalIgnoreCase);
        nodesByDir[""] = [];

        // Seed every on-disk directory under `.tap/` so empty folders (just created, or
        // emptied by deletion) still surface in the tree as grouping nodes.
        if (Directory.Exists(ws.TapDirectory))
        {
            foreach (var dirAbs in Directory.EnumerateDirectories(ws.TapDirectory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(ws.TapDirectory, dirAbs).Replace('\\', '/');
                if (rel.Length == 0) continue;
                EnsureDir(nodesByDir, rel);
            }
        }

        foreach (var f in ws.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Kind == WorkspaceKind.Workspace) continue; // tap.md surfaced via /api/workspace
            var dir = Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/') ?? string.Empty;
            EnsureDir(nodesByDir, dir);

            var node = new TreeNodeDto(
                Path: f.RelativePath,
                Kind: f.Kind.ToString().ToLowerInvariant(),
                Name: f.Name ?? Path.GetFileNameWithoutExtension(f.RelativePath),
                Id: f.Id,
                Children: []);
            nodesByDir[dir].Add(node);
        }

        // Compose directories as synthetic nodes so the UI can render a single tree.
        // Directories are pure grouping — name comes from the last path segment.
        var rootChildren = new List<TreeNodeDto>(nodesByDir[""]);
        foreach (var (dir, nodes) in nodesByDir.Where(kv => kv.Key != "").OrderBy(kv => kv.Key))
        {
            var parts = dir.Split('/');
            var parentDir = string.Join('/', parts.Take(parts.Length - 1));

            var dirNode = new TreeNodeDto(
                Path: dir,
                Kind: "directory",
                Name: parts[^1],
                Id: null,
                Children: nodes);
            if (parentDir == "") rootChildren.Add(dirNode);
            else
            {
                EnsureDir(nodesByDir, parentDir);
                nodesByDir[parentDir].Add(dirNode);
            }
        }
        return rootChildren;
    }

    private static void EnsureDir(Dictionary<string, List<TreeNodeDto>> map, string dir)
    {
        if (!map.ContainsKey(dir)) map[dir] = [];
    }

    private static bool TryResolveWorkspacePath(WorkspaceService svc, string relative,
        out string full, out string error)
        => WorkspacePathResolver.TryResolve(svc, relative, out full, out error);

    /// <summary>Re-runs <see cref="GitInspector.Inspect(string)"/> so branch + remote URLs
    /// are fresh on every list call. Falls back to the workspace path when no persisted git
    /// root was available yet (e.g. repo initialized after the workspace was first added).</summary>
    private static GitInfoDto? BuildGitDto(string workspacePath, string? gitRoot)
    {
        var probe = string.IsNullOrEmpty(gitRoot) ? workspacePath : gitRoot;
        var info = GitInspector.Inspect(probe);
        if (info is null) return null;
        return new GitInfoDto(
            Root: info.Root,
            Branch: info.Branch,
            IsDetached: info.IsDetached,
            OriginUrl: info.OriginUrl,
            Remotes: info.Remotes.Select(r => new GitRemoteDto(r.Name, r.Url)).ToArray());
    }
}
