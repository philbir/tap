using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Workspace;

/// <summary>
/// Loads a workspace from disk: walks the selected workspace root folder, parses every file
/// matching the known suffixes (§2), and builds an in-memory index keyed by relative path
/// and by id.
///
/// I/O lives here; <see cref="FileParser"/> stays pure so it can be unit-tested without disk.
/// </summary>
public sealed class WorkspaceLoader
{
    /// <summary>Legacy folder name used by older workspace layouts.</summary>
    public const string TapDirectoryName = ".tap";
    public const string ManifestFileName = "tap.md";

    /// <summary>Loads the workspace rooted at <paramref name="rootDirectory"/>.</summary>
    public LoadedWorkspace Load(string rootDirectory)
    {
        var tapDir = rootDirectory;
        if (!Directory.Exists(tapDir))
            throw new DirectoryNotFoundException($"Workspace directory '{tapDir}' does not exist.");

        var files = new List<WorkspaceFile>();
        var errors = new List<WorkspaceError>();

        foreach (var path in Directory.EnumerateFiles(tapDir, "*.md", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(path);
            if (KindResolver.FromFileName(fileName) is null)
                continue; // README.md etc. — silently skipped.

            var relative = Path.GetRelativePath(tapDir, path).Replace('\\', '/');
            try
            {
                var content = File.ReadAllText(path);
                files.Add(FileParser.Parse(relative, content));
            }
            catch (WorkspaceParseException ex)
            {
                errors.Add(ex.Error);
            }
        }

        return new LoadedWorkspace(rootDirectory, tapDir, files, errors);
    }
}

/// <summary>
/// Loaded, indexed workspace. The owner of every parsed <see cref="WorkspaceFile"/>.
/// Indexes are built eagerly; consumers should treat this as immutable per load — invalidate
/// and reload on file system changes.
/// </summary>
public sealed class LoadedWorkspace
{
    private readonly Dictionary<string, WorkspaceFile> _byPath;
    private readonly Dictionary<string, WorkspaceFile> _byId;

    public LoadedWorkspace(string rootDirectory, string tapDirectory, IReadOnlyList<WorkspaceFile> files, IReadOnlyList<WorkspaceError> errors)
    {
        RootDirectory = rootDirectory;
        TapDirectory = tapDirectory;
        Files = files;
        Errors = errors;
        _byPath = files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        _byId = files.Where(f => f.Id is not null).ToDictionary(f => f.Id!, StringComparer.OrdinalIgnoreCase);

        Manifest = files.OfType<WorkspaceManifestFile>().FirstOrDefault();
        Requests = files.OfType<RequestFile>().ToArray();
        Auths = files.OfType<AuthFile>().ToArray();
        Environments = files.OfType<EnvFile>().ToArray();
        Collections = files.OfType<CollectionFile>().ToArray();
    }

    public string RootDirectory { get; }
    public string TapDirectory { get; }
    public IReadOnlyList<WorkspaceFile> Files { get; }
    public IReadOnlyList<WorkspaceError> Errors { get; }

    public WorkspaceManifestFile? Manifest { get; }
    public IReadOnlyList<RequestFile> Requests { get; }
    public IReadOnlyList<AuthFile> Auths { get; }
    public IReadOnlyList<EnvFile> Environments { get; }
    public IReadOnlyList<CollectionFile> Collections { get; }

    public WorkspaceFile? FindByPath(string relativePath)
        => _byPath.GetValueOrDefault(relativePath.Replace('\\', '/'));

    public WorkspaceFile? FindById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Resolves a <see cref="WorkspaceRef"/> using its path (if set) then its id.</summary>
    public WorkspaceFile? Resolve(WorkspaceRef? r, string? sourceRelativeDir = null)
    {
        if (r is null) return null;

        if (r.RelativePath is not null)
        {
            // Path is relative to the *file that declared the ref*, not the workspace root.
            var combined = sourceRelativeDir is null
                ? r.RelativePath
                : Path.Combine(sourceRelativeDir, r.RelativePath).Replace('\\', '/');
            var normalized = NormalizeRelative(combined);
            if (_byPath.TryGetValue(normalized, out var hit)) return hit;
        }

        if (r.Id is not null && _byId.TryGetValue(r.Id, out var byId))
            return byId;

        return null;
    }

    private static string NormalizeRelative(string p)
    {
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var part in parts)
        {
            if (part == "..") { if (stack.Count > 0) stack.Pop(); }
            else if (part != ".") stack.Push(part);
        }
        return string.Join('/', stack.Reverse());
    }
}
