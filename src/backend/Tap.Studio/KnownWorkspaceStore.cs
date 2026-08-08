using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace;

namespace Tap.Studio;

/// <summary>
/// Tracks the set of workspaces the user has opened, plus which one is currently active.
/// Persisted as JSON next to the studio state file so it survives restarts and is shared across
/// every Studio process for the same user profile.
///
/// The store is the source of truth for the header's workspace switcher. Adding a workspace
/// here bootstraps a <c>tap.md</c> manifest in the selected folder when missing, then
/// registers a pointer to it.
/// </summary>
public sealed class KnownWorkspaceStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private KnownWorkspacesFile _state;
    private DateTime _loadedWriteTimeUtc;

    public KnownWorkspaceStore(StudioOptions options)
    {
        // Sit alongside Studio:StatePath so the user only has one folder to clean up.
        var dir = Path.GetDirectoryName(options.StatePath) ?? Path.GetTempPath();
        _path = Path.Combine(dir, "workspaces.json");

        // The desktop shell's default root (<system dir>/workspace) belongs to Studio, so it
        // creates and scaffolds it on first run. A root the user or a host configured explicitly
        // is never created behind their back — a typo there should surface as an error, not as
        // a silently invented folder.
        if (options.DesktopShell && !Directory.Exists(options.WorkspaceRoot))
        {
            try
            {
                Directory.CreateDirectory(options.WorkspaceRoot);
                EnsureWorkspaceScaffold(options.WorkspaceRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through: the loader reports the unreadable root and the UI offers a switcher.
            }
        }

        _state = Load(_path) ?? new KnownWorkspacesFile { Active = options.WorkspaceRoot, Workspaces = [] };
        _loadedWriteTimeUtc = SafeWriteTimeUtc(_path);

        // Prune workspaces whose folder has since been deleted or moved. A stale
        // pointer only 500s on the next read and clutters the switcher (e.g. after
        // a repo reorg leaves an old project path behind). The boot workspace is
        // re-registered right after and the active path is healed below, so this
        // never drops something still in use.
        var pruned = _state.Workspaces.Where(w => Directory.Exists(w.Path) && !IsFilesystemRoot(w.Path)).ToArray();
        var changed = pruned.Length != _state.Workspaces.Count;
        if (changed) _state = _state with { Workspaces = pruned };

        // Ensure the boot workspace is always represented — even if the user wiped the JSON.
        changed |= EnsureRegistered(options.WorkspaceRoot);

        // Heal a stale active path (folder removed since last run) by falling back to
        // the options-supplied root. The user can switch again from the UI.
        var activeStillValid = !string.IsNullOrEmpty(_state.Active)
            && Directory.Exists(_state.Active)
            && !IsFilesystemRoot(_state.Active);
        if (!activeStillValid)
        {
            _state = _state with { Active = options.WorkspaceRoot };
            changed = true;
        }

        // Backfill GitRoot for workspaces persisted before git tracking existed. Re-running
        // discovery is cheap; the alternative (lazy on first read) would force every list
        // request to walk the filesystem.
        if (_state.Workspaces.Any(w => w.GitRoot is null))
        {
            var backfilled = _state.Workspaces.Select(w => w.GitRoot is null
                ? w with { GitRoot = GitInspector.FindGitRoot(w.Path) }
                : w).ToArray();
            _state = _state with { Workspaces = backfilled };
            changed = true;
        }

        // Write only when boot actually changed something. Persisting unconditionally meant a
        // second Studio process starting up stamped its own startup snapshot over whatever the
        // first one had registered in the meantime.
        if (changed) Persist();
    }

    /// <summary>Re-reads <c>workspaces.json</c> when its on-disk timestamp moved past what we
    /// loaded. Several Studio processes routinely share <c>~/.tap</c> (desktop app + a dev
    /// instance); without this, an instance keeps serving — and on the next save, writes back —
    /// a stale list, silently dropping workspaces another instance registered. Must be called
    /// under <see cref="_gate"/>.</summary>
    private void ReloadIfChangedLocked()
    {
        var mtime = SafeWriteTimeUtc(_path);
        if (mtime == _loadedWriteTimeUtc) return;
        if (Load(_path) is { } fresh)
        {
            // Adopt the other instance's list but keep our own active pointer — the desktop
            // app and a dev instance are allowed to sit on different workspaces, and swapping
            // Active out from under a live session would reload the entire UI mid-edit.
            _state = fresh with { Active = _state.Active };
            _loadedWriteTimeUtc = mtime;
        }
    }

    private static DateTime SafeWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public IReadOnlyList<KnownWorkspace> List()
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            return [.. _state.Workspaces];
        }
    }

    public string ActivePath
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChangedLocked();
                return _state.Active;
            }
        }
    }

    public KnownWorkspace Add(string fullPath)
    {
        var canonical = Path.GetFullPath(fullPath);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException($"Folder '{canonical}' does not exist.");

        // Bootstrap a manifest so switching into a brand-new folder always has a
        // workspace definition for the editor/API.
        EnsureWorkspaceScaffold(canonical);

        lock (_gate)
        {
            ReloadIfChangedLocked();
            EnsureRegistered(canonical);
            Persist();
            return _state.Workspaces.First(w => PathsEqual(w.Path, canonical));
        }
    }

    public void Remove(string fullPath)
    {
        var canonical = Path.GetFullPath(fullPath);
        lock (_gate)
        {
            ReloadIfChangedLocked();
            // Refuse to remove the active one — caller must activate something else first.
            if (PathsEqual(_state.Active, canonical))
                throw new InvalidOperationException("Cannot remove the active workspace. Switch to another first.");

            _state = _state with { Workspaces = [.. _state.Workspaces.Where(w => !PathsEqual(w.Path, canonical))] };
            Persist();
        }
    }

    public void Activate(string fullPath)
    {
        var canonical = Path.GetFullPath(fullPath);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException($"Folder '{canonical}' does not exist.");

        EnsureWorkspaceScaffold(canonical);

        lock (_gate)
        {
            ReloadIfChangedLocked();
            EnsureRegistered(canonical);
            _state = _state with { Active = canonical };
            Persist();
        }
    }

    /// <summary>Bootstrap a workspace manifest in the selected folder if missing.</summary>
    private static void EnsureWorkspaceScaffold(string root)
    {
        var manifestPath = Path.Combine(root, WorkspaceLoader.ManifestFileName);
        if (File.Exists(manifestPath)) return;

        var folderName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var spec = new WorkspaceSpecDto
        {
            Name = string.IsNullOrWhiteSpace(folderName) ? "workspace" : folderName,
        };
        File.WriteAllText(manifestPath, WorkspaceSpecEmitter.ToFileSource(spec));
    }

    /// <summary>Adds <paramref name="canonical"/> to the list when it isn't already there.
    /// Returns whether the state actually changed, so the constructor can skip a write it
    /// doesn't need.</summary>
    private bool EnsureRegistered(string canonical)
    {
        if (_state.Workspaces.Any(w => PathsEqual(w.Path, canonical))) return false;
        // Discover the enclosing git repo once at add-time and pin its root path. Branch +
        // remote URLs are computed fresh on every read (those change as the user works) but
        // the discovery walk is the slow part and the root rarely moves.
        var gitRoot = GitInspector.FindGitRoot(canonical);
        var label = Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _state = _state with
        {
            Workspaces = [.. _state.Workspaces, new KnownWorkspace(canonical, label, gitRoot)],
        };
        return true;
    }

    private void Persist()
    {
        AtomicStateFile.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicStateFile.WriteAllText(_path, JsonSerializer.Serialize(_state, KnownWorkspaceJson.Default.KnownWorkspacesFile));
        _loadedWriteTimeUtc = SafeWriteTimeUtc(_path);
    }

    private static KnownWorkspacesFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), KnownWorkspaceJson.Default.KnownWorkspacesFile); }
        catch { return null; }
    }

    /// <summary>
    /// True for <c>/</c>, <c>C:\</c> and friends. A filesystem root is never a workspace anyone
    /// chose — it is what a working-directory default resolves to for an app launched from
    /// Finder or the Dock, and older desktop builds persisted it here on first run. Loading it
    /// means walking the whole disk, so it is pruned from the list and refused as the active
    /// path, which un-wedges machines that already have one recorded.
    /// </summary>
    private static bool IsFilesystemRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        return full.Length > 0 && PathsEqual(full, root);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record KnownWorkspace(string Path, string Label, string? GitRoot = null);

public sealed record KnownWorkspacesFile
{
    public required string Active { get; init; }
    public required IReadOnlyList<KnownWorkspace> Workspaces { get; init; }
}

[JsonSerializable(typeof(KnownWorkspacesFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class KnownWorkspaceJson : JsonSerializerContext
{
}
