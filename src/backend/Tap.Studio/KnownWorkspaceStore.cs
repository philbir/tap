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

    public KnownWorkspaceStore(StudioOptions options)
    {
        // Sit alongside Studio:StatePath so the user only has one folder to clean up.
        var dir = Path.GetDirectoryName(options.StatePath) ?? Path.GetTempPath();
        _path = Path.Combine(dir, "workspaces.json");
        _state = Load(_path) ?? new KnownWorkspacesFile { Active = options.WorkspaceRoot, Workspaces = [] };

        // Prune workspaces whose folder has since been deleted or moved. A stale
        // pointer only 500s on the next read and clutters the switcher (e.g. after
        // a repo reorg leaves an old project path behind). The boot workspace is
        // re-registered right after and the active path is healed below, so this
        // never drops something still in use.
        _state = _state with
        {
            Workspaces = [.. _state.Workspaces.Where(w => Directory.Exists(w.Path))]
        };

        // Ensure the boot workspace is always represented — even if the user wiped the JSON.
        EnsureRegistered(options.WorkspaceRoot);

        // Heal a stale active path (folder removed since last run) by falling back to
        // the options-supplied root. The user can switch again from the UI.
        var activeStillValid = !string.IsNullOrEmpty(_state.Active)
            && Directory.Exists(_state.Active);
        if (!activeStillValid) _state = _state with { Active = options.WorkspaceRoot };

        // Backfill GitRoot for workspaces persisted before git tracking existed. Re-running
        // discovery is cheap; the alternative (lazy on first read) would force every list
        // request to walk the filesystem.
        var backfilled = _state.Workspaces.Select(w => w.GitRoot is null
            ? w with { GitRoot = GitInspector.FindGitRoot(w.Path) }
            : w).ToArray();
        _state = _state with { Workspaces = backfilled };

        Persist();
    }

    public IReadOnlyList<KnownWorkspace> List()
    {
        lock (_gate) return [.. _state.Workspaces];
    }

    public string ActivePath
    {
        get { lock (_gate) return _state.Active; }
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

    private void EnsureRegistered(string canonical)
    {
        if (_state.Workspaces.Any(w => PathsEqual(w.Path, canonical))) return;
        // Discover the enclosing git repo once at add-time and pin its root path. Branch +
        // remote URLs are computed fresh on every read (those change as the user works) but
        // the discovery walk is the slow part and the root rarely moves.
        var gitRoot = GitInspector.FindGitRoot(canonical);
        var label = Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _state = _state with
        {
            Workspaces = [.. _state.Workspaces, new KnownWorkspace(canonical, label, gitRoot)],
        };
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_state, KnownWorkspaceJson.Default.KnownWorkspacesFile));
    }

    private static KnownWorkspacesFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), KnownWorkspaceJson.Default.KnownWorkspacesFile); }
        catch { return null; }
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
