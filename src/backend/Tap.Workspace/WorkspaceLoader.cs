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

    /// <summary>
    /// Ceiling on the size of a single workspace file. A workspace is whatever the developer
    /// cloned, so every <c>*.md</c> in it is untrusted input; without a cap a multi-gigabyte file
    /// would be pulled into memory whole by the <c>ReadAllText</c> below.
    /// </summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Folder count at which the walk stops and reports a truncated workspace. See
    /// <see cref="ScanBudget"/> for why there is a ceiling at all; this one covers the case
    /// where the tree is enormous but every directory read is fast.
    /// </summary>
    public const int MaxDirectories = 25_000;

    /// <summary>
    /// Wall-clock ceiling on the folder walk. A workspace root is just a folder the user
    /// picked — or, in the desktop app before it knows better, whatever the process's working
    /// directory happened to be, which for a Finder-launched <c>.app</c> is <c>/</c>. Walking
    /// that means walking the whole disk (network mounts and iCloud placeholders included),
    /// which reads as "Tap Studio hangs on startup". A truncated workspace with an error the
    /// UI can show beats an unbounded scan every time.
    /// </summary>
    public static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Folders never worth descending into: package caches and VCS metadata hold no Tap files
    /// but plenty of the deepest trees on the machine. Everything else — dotfolders included,
    /// since <c>.tap/</c> is a supported layout — is walked normally.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", ".venv", "__pycache__", "bin", "obj", "target",
    };

    /// <summary>Loads the workspace rooted at <paramref name="rootDirectory"/>.</summary>
    public LoadedWorkspace Load(string rootDirectory)
    {
        var tapDir = rootDirectory;
        if (!Directory.Exists(tapDir))
            throw new DirectoryNotFoundException($"Workspace directory '{tapDir}' does not exist.");

        var files = new List<WorkspaceFile>();
        var errors = new List<WorkspaceError>();

        var scan = ScanMarkdown(tapDir);
        if (scan.Truncated)
        {
            errors.Add(new WorkspaceError(
                WorkspaceErrorCode.E_WORKSPACE_SCAN_TRUNCATED,
                $"Stopped scanning after {scan.Directories} folders / {scan.Elapsed.TotalSeconds:F1}s — " +
                $"'{tapDir}' is too large to be a workspace root, so it was only partially loaded. " +
                "Switch to the folder that actually holds your Tap files."));
        }

        foreach (var path in scan.Files)
        {
            var fileName = Path.GetFileName(path);
            if (KindResolver.FromFileName(fileName) is null)
                continue; // README.md etc. — silently skipped.

            var relative = Path.GetRelativePath(tapDir, path).Replace('\\', '/');
            try
            {
                var length = new FileInfo(path).Length;
                if (length > MaxFileBytes)
                {
                    errors.Add(new WorkspaceError(
                        WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                        $"File is {length} bytes; workspace files are capped at {MaxFileBytes} bytes and this one was not read.",
                        relative));
                    continue;
                }

                var content = File.ReadAllText(path);
                files.Add(FileParser.Parse(relative, content));
            }
            catch (WorkspaceParseException ex)
            {
                errors.Add(ex.Error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished mid-walk or that we can't read is a bad file, not a bad
                // workspace — the loader must still return the rest of it.
                errors.Add(new WorkspaceError(
                    WorkspaceErrorCode.E_FRONTMATTER_MALFORMED_YAML,
                    $"Could not read file: {ex.Message}",
                    relative));
            }
        }

        return new LoadedWorkspace(rootDirectory, tapDir, files, errors);
    }

    /// <summary>Result of the bounded folder walk: the <c>*.md</c> paths found, and whether we
    /// ran out of budget before covering the tree.</summary>
    private readonly record struct ScanResult(
        IReadOnlyList<string> Files,
        int Directories,
        TimeSpan Elapsed,
        bool Truncated);

    /// <summary>
    /// Walks <paramref name="root"/> for <c>*.md</c> files under <see cref="ScanBudget"/> /
    /// <see cref="MaxDirectories"/>.
    ///
    /// <para>Hand-rolled rather than <c>EnumerateFiles(…, SearchOption.AllDirectories)</c> for
    /// three reasons: the budget has to be checked between directories, <see cref="SkippedDirectories"/>
    /// has to be honoured before descending, and symlinked directories have to be left alone —
    /// a link that points at an ancestor (or at <c>/</c>) turns one wrong root into a walk that
    /// never ends. Unreadable folders are skipped: a permission-denied subtree is not a reason
    /// to fail the whole workspace.</para>
    /// </summary>
    private static ScanResult ScanMarkdown(string root)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var directories = 0;

        while (pending.Count > 0)
        {
            if (directories >= MaxDirectories || clock.Elapsed > ScanBudget)
                return new ScanResult(files, directories, clock.Elapsed, Truncated: true);

            var current = pending.Pop();
            directories++;
            try
            {
                files.AddRange(Directory.EnumerateFiles(current, "*.md"));

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (SkippedDirectories.Contains(Path.GetFileName(child)))
                        continue;
                    if (new DirectoryInfo(child).LinkTarget is not null)
                        continue;
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Vanished mid-walk, or not ours to read.
            }
        }

        return new ScanResult(files, directories, clock.Elapsed, Truncated: false);
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

        // Both indexes are case-insensitive, so two files whose paths differ only in case — or two
        // files declaring the same id — used to make ToDictionary throw straight out of the
        // constructor. A workspace is untrusted input; a collision has to degrade to "loaded with
        // errors", never take the host down. First one wins, the rest are reported.
        var allErrors = new List<WorkspaceError>(errors);
        _byPath = new Dictionary<string, WorkspaceFile>(files.Count, StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, WorkspaceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!_byPath.TryAdd(f.RelativePath, f))
            {
                allErrors.Add(new WorkspaceError(
                    WorkspaceErrorCode.E_UNKNOWN_FIELD,
                    $"Path collides with '{_byPath[f.RelativePath].RelativePath}' (paths are matched case-insensitively). This file is not reachable by path.",
                    f.RelativePath));
            }

            if (f.Id is { } id && !_byId.TryAdd(id, f))
            {
                allErrors.Add(new WorkspaceError(
                    WorkspaceErrorCode.E_UNKNOWN_FIELD,
                    $"Duplicate id '{id}' — already declared by '{_byId[id].RelativePath}'. This file is not reachable by id.",
                    f.RelativePath));
            }
        }
        Errors = allErrors;

        Manifest = files.OfType<WorkspaceManifestFile>().FirstOrDefault();
        Requests = files.OfType<RequestFile>().ToArray();
        Auths = files.OfType<AuthFile>().ToArray();
        Environments = files.OfType<EnvFile>().ToArray();
        Collections = files.OfType<CollectionFile>().ToArray();
        Flows = files.OfType<FlowFile>().ToArray();
        TestSets = files.OfType<TestSetFile>().ToArray();
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
    public IReadOnlyList<FlowFile> Flows { get; }
    public IReadOnlyList<TestSetFile> TestSets { get; }

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
