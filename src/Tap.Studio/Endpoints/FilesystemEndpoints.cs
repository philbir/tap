using Tap.Studio.Contracts;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/fs/*</c> — read-only filesystem inspection endpoints, used by the workspace
/// picker dialog. We can't ride on browser file pickers (the SPA runs in a desktop shell
/// where they're not always available), so the Studio backend lists folders on demand and
/// the UI renders a tiny breadcrumb-style navigator.
///
/// <para>These endpoints expose the whole filesystem the Studio process can read. That's
/// fine because Studio is a developer tool bound to loopback; the host warning in
/// <see cref="StudioHost"/> already covers non-loopback exposure.</para>
/// </summary>
public static class FilesystemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/fs");

        // Browse a directory. Empty path or null falls back to the user's home directory.
        // Returns the canonical path the server actually listed (so the UI can sync its
        // breadcrumb), the parent path (or null at filesystem root), plus the subdirectories.
        g.MapGet("/browse", (string? path) =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var target = string.IsNullOrWhiteSpace(path) ? home : path!;
            try { target = Path.GetFullPath(target); }
            catch { return Results.BadRequest(new { code = "invalid-path", message = "Path is not a valid filesystem path." }); }

            if (!Directory.Exists(target))
                return Results.BadRequest(new { code = "not-found", message = $"Directory '{target}' does not exist." });

            IEnumerable<string> directoryNames;
            try
            {
                directoryNames = Directory.EnumerateDirectories(target);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new { code = "denied", message = ex.Message });
            }

            var entries = new List<DirectoryEntryDto>();
            foreach (var dirAbs in directoryNames)
            {
                var name = Path.GetFileName(dirAbs);
                if (string.IsNullOrEmpty(name)) continue;

                // Skip the typical "hidden" dotfiles unless the caller explicitly navigated
                // into one — they're noise in a picker (.DS_Store-style folders, .Trash).
                // Keep `.tap` visible so the user can see what's already a workspace.
                if (name.StartsWith('.') && !string.Equals(name, ".tap", StringComparison.Ordinal))
                    continue;

                bool hasTap = false;
                try { hasTap = Directory.Exists(Path.Combine(dirAbs, ".tap")); }
                catch { /* unreadable — leave hasTap=false */ }

                entries.Add(new DirectoryEntryDto(name, Path.GetFullPath(dirAbs), hasTap));
            }

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var parent = Path.GetDirectoryName(target);
            // GetDirectoryName returns "" for a root path on Unix and null on Windows roots.
            var hasParent = !string.IsNullOrEmpty(parent) && !string.Equals(parent, target, StringComparison.Ordinal);

            var isWorkspace = Directory.Exists(Path.Combine(target, ".tap"));
            var git = GitInspector.Inspect(target);

            return Results.Ok(new BrowseResponseDto(
                Path: target,
                Parent: hasParent ? parent : null,
                Home: home,
                IsWorkspace: isWorkspace,
                GitRoot: git?.Root,
                Entries: entries));
        });

        // Create a new directory under a parent path. The picker calls this to let users
        // organize while they navigate (e.g. mkdir a project folder before they've added
        // its `.tap/` next to it). Returns the canonical absolute path of the new folder
        // so the UI can immediately descend into it.
        g.MapPost("/folders", (CreateDirectoryDto body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Parent))
                return Results.BadRequest(new { code = "invalid-path", message = "Parent path is required." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { code = "invalid-name", message = "Folder name is required." });

            // Reject anything that looks like a path segment instead of a leaf name. Without
            // this a caller could pass `../../etc` and escape the chosen parent — Path.Combine
            // would happily compose it. The picker only ever wants a single new leaf.
            var name = body.Name.Trim();
            if (name.IndexOfAny(['/', '\\']) >= 0
                || name.Contains("..", StringComparison.Ordinal)
                || name is "." or "")
                return Results.BadRequest(new { code = "invalid-name", message = "Folder name cannot contain path separators or '..'." });

            string parentFull;
            try { parentFull = Path.GetFullPath(body.Parent); }
            catch { return Results.BadRequest(new { code = "invalid-path", message = "Parent path is not a valid filesystem path." }); }

            if (!Directory.Exists(parentFull))
                return Results.BadRequest(new { code = "not-found", message = $"Parent directory '{parentFull}' does not exist." });

            var full = Path.Combine(parentFull, name);
            if (Directory.Exists(full) || File.Exists(full))
                return Results.BadRequest(new { code = "exists", message = "An entry with that name already exists." });

            try { Directory.CreateDirectory(full); }
            catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { code = "denied", message = ex.Message }); }
            catch (IOException ex) { return Results.BadRequest(new { code = "io", message = ex.Message }); }

            return Results.Ok(new CreateDirectoryResponseDto(Path: Path.GetFullPath(full)));
        });
    }
}
