using System.Diagnostics;
using System.Text;
using LibGit2Sharp;
using Tap.Studio.Contracts;

namespace Tap.Studio;

/// <summary>
/// Per-request git operations against the repository enclosing the active workspace.
///
/// <para>Read + local-write operations (status, stage, unstage, commit, branch create/checkout,
/// diff) go through <see cref="LibGit2Sharp"/>. Network operations (fetch / pull / push) shell
/// out to the user's installed <c>git</c> binary so the existing credential helper, SSH agent,
/// signing config, hooks, and <c>~/.gitconfig</c> machinery all just work — LibGit2Sharp's
/// own transport stack would force us to reimplement all of that.</para>
/// </summary>
public sealed class GitService
{
    private readonly WorkspaceService _workspace;

    public GitService(WorkspaceService workspace)
    {
        _workspace = workspace;
    }

    /// <summary>Returns the git working-tree root for the active workspace, or null when the
    /// workspace isn't inside a git repository.</summary>
    public string? Root => GitInspector.FindGitRoot(_workspace.RootDirectory);

    public GitStatusDto? GetStatus()
    {
        var root = Root;
        if (root is null) return null;

        using var repo = new Repository(root);
        var head = repo.Head;
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        var trackingDetails = head.TrackingDetails;
        // FriendlyName ("origin/dreamr-wedding") is what users see in `git status`; the
        // canonical "refs/heads/..." form is a transport detail.
        var upstream = head.TrackedBranch?.FriendlyName;

        return new GitStatusDto(
            Root: root,
            Branch: head.FriendlyName,
            IsDetached: repo.Info.IsHeadDetached,
            HasRemote: repo.Network.Remotes.Any(),
            Upstream: upstream,
            Ahead: trackingDetails?.AheadBy,
            Behind: trackingDetails?.BehindBy,
            StagedCount: CountStaged(status),
            UnstagedCount: CountUnstaged(status));
    }

    public IReadOnlyList<GitFileChangeDto> GetChanges()
    {
        var root = Root;
        if (root is null) return [];
        using var repo = new Repository(root);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        var list = new List<GitFileChangeDto>();
        foreach (var entry in status)
        {
            // Skip "Ignored" / "Nonexistent" — they aren't actionable in the UI.
            var (index, working) = ClassifyStatus(entry.State);
            if (index is null && working is null) continue;
            list.Add(new GitFileChangeDto(
                Path: NormalizeSeparators(entry.FilePath),
                IndexStatus: index,
                WorkingStatus: working));
        }
        list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public IReadOnlyList<GitBranchDto> GetBranches()
    {
        var root = Root;
        if (root is null) return [];
        using var repo = new Repository(root);
        var currentName = repo.Head.FriendlyName;
        var branches = new List<GitBranchDto>();
        foreach (var b in repo.Branches.OrderBy(b => b.IsRemote).ThenBy(b => b.FriendlyName))
        {
            branches.Add(new GitBranchDto(
                Name: b.FriendlyName,
                IsCurrent: !b.IsRemote && string.Equals(b.FriendlyName, currentName, StringComparison.Ordinal),
                IsRemote: b.IsRemote,
                Upstream: b.IsTracking ? b.TrackedBranch?.FriendlyName : null,
                Tip: b.Tip?.Sha[..7]));
        }
        return branches;
    }

    public void Stage(IEnumerable<string> paths)
    {
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);
        // Commands.Stage handles untracked, modified, and deleted (it adds and removes).
        var arr = paths.Select(NormalizeSeparators).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (arr.Length == 0) return;
        Commands.Stage(repo, arr);
    }

    public void Unstage(IEnumerable<string> paths)
    {
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);
        var arr = paths.Select(NormalizeSeparators).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (arr.Length == 0) return;
        Commands.Unstage(repo, arr);
    }

    /// <summary>
    /// Discard working-tree changes for the given paths.
    /// <list type="bullet">
    ///   <item>Untracked files are deleted from disk.</item>
    ///   <item>Tracked modifications/deletions are reverted via
    ///         <c>repo.CheckoutPaths("HEAD", …, Force)</c>.</item>
    /// </list>
    /// Staged-only paths aren't touched here — they need <see cref="Unstage"/> first.
    /// </summary>
    public void Discard(IEnumerable<string> paths)
    {
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        var normalised = paths.Select(NormalizeSeparators).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (normalised.Length == 0) return;
        using var repo = new Repository(root);

        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
        var byPath = status.ToDictionary(e => e.FilePath, e => e.State, StringComparer.OrdinalIgnoreCase);

        var trackedToRestore = new List<string>();
        foreach (var p in normalised)
        {
            var state = byPath.GetValueOrDefault(p, FileStatus.Unaltered);
            if (state.HasFlag(FileStatus.NewInWorkdir))
            {
                // Untracked — just remove from disk.
                var full = Path.Combine(root, p);
                if (File.Exists(full)) File.Delete(full);
            }
            else
            {
                trackedToRestore.Add(p);
            }
        }

        if (trackedToRestore.Count > 0)
        {
            repo.CheckoutPaths("HEAD", trackedToRestore, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force,
            });
        }
    }

    public GitCommitResultDto Commit(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("Commit message cannot be empty.");
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);

        Signature signature;
        try
        {
            signature = repo.Config.BuildSignature(DateTimeOffset.Now)
                ?? throw new InvalidOperationException(
                    "git user.name / user.email is not configured. Set them with `git config --global user.name` and `user.email`.");
        }
        catch (LibGit2SharpException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var commit = repo.Commit(message, signature, signature, new CommitOptions { AllowEmptyCommit = false });
        return new GitCommitResultDto(Sha: commit.Sha, ShortSha: commit.Sha[..7], Message: commit.MessageShort);
    }

    public void CreateBranch(string name, bool checkout)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Branch name is required.");
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);
        if (repo.Branches[name] is not null)
            throw new InvalidOperationException($"Branch '{name}' already exists.");

        var branch = repo.CreateBranch(name);
        if (checkout) Commands.Checkout(repo, branch);
    }

    public void Checkout(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Branch name is required.");
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);

        var branch = repo.Branches[name];
        if (branch is null)
        {
            // Allow checkout of a remote tracking branch by creating a matching local one
            // — what `git checkout <name>` does for `origin/<name>`.
            var remote = repo.Branches[$"origin/{name}"];
            if (remote is null)
                throw new InvalidOperationException($"Branch '{name}' does not exist.");
            var local = repo.CreateBranch(name, remote.Tip);
            repo.Branches.Update(local, b => b.TrackedBranch = remote.CanonicalName);
            branch = local;
        }
        Commands.Checkout(repo, branch);
    }

    public string Diff(string path, bool staged)
    {
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);
        var normalized = NormalizeSeparators(path);
        string[] paths = [normalized];
        Patch patch;
        if (staged)
        {
            patch = repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index, paths);
        }
        else
        {
            // Unstaged: index vs working tree, including untracked files.
            patch = repo.Diff.Compare<Patch>(paths, includeUntracked: true);
        }
        return patch.Content;
    }

    public Task<GitCommandResultDto> FetchAsync(CancellationToken ct)
        => RunGitAsync(["fetch", "--prune"], ct);

    public Task<GitCommandResultDto> PullAsync(CancellationToken ct)
        => RunGitAsync(["pull", "--ff-only"], ct);

    public Task<GitCommandResultDto> PushAsync(bool setUpstream, CancellationToken ct)
    {
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        using var repo = new Repository(root);
        var head = repo.Head;
        if (head.IsTracking || !setUpstream)
            return RunGitAsync(["push"], ct);
        // First push of a fresh local branch — set upstream to origin so subsequent pushes
        // don't need a flag.
        var hasOrigin = repo.Network.Remotes.Any(r => r.Name == "origin");
        if (!hasOrigin)
            throw new InvalidOperationException("No 'origin' remote configured.");
        return RunGitAsync(["push", "--set-upstream", "origin", head.FriendlyName], ct);
    }

    private async Task<GitCommandResultDto> RunGitAsync(string[] args, CancellationToken ct)
    {
        var root = Root ?? throw new InvalidOperationException("Workspace is not in a git repository.");
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch 'git'. Is it on PATH?");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadAllAsync(proc.StandardOutput, stdout, ct);
        var stderrTask = ReadAllAsync(proc.StandardError, stderr, ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return new GitCommandResultDto(
            ExitCode: proc.ExitCode,
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString());
    }

    private static async Task ReadAllAsync(StreamReader r, StringBuilder sink, CancellationToken ct)
    {
        var buf = new char[4096];
        while (!ct.IsCancellationRequested)
        {
            var n = await r.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            sink.Append(buf, 0, n);
        }
    }

    private static string NormalizeSeparators(string p) => p.Replace('\\', '/');

    private static (string? index, string? working) ClassifyStatus(FileStatus state)
    {
        string? index = null;
        string? working = null;

        if (state.HasFlag(FileStatus.NewInIndex)) index = "added";
        else if (state.HasFlag(FileStatus.ModifiedInIndex)) index = "modified";
        else if (state.HasFlag(FileStatus.DeletedFromIndex)) index = "deleted";
        else if (state.HasFlag(FileStatus.RenamedInIndex)) index = "renamed";
        else if (state.HasFlag(FileStatus.TypeChangeInIndex)) index = "typechange";

        if (state.HasFlag(FileStatus.NewInWorkdir)) working = "untracked";
        else if (state.HasFlag(FileStatus.ModifiedInWorkdir)) working = "modified";
        else if (state.HasFlag(FileStatus.DeletedFromWorkdir)) working = "deleted";
        else if (state.HasFlag(FileStatus.RenamedInWorkdir)) working = "renamed";
        else if (state.HasFlag(FileStatus.TypeChangeInWorkdir)) working = "typechange";
        else if (state.HasFlag(FileStatus.Conflicted)) working = "conflicted";

        return (index, working);
    }

    private static int CountStaged(RepositoryStatus s) => s.Added.Count() + s.Staged.Count() + s.Removed.Count() + s.RenamedInIndex.Count();
    private static int CountUnstaged(RepositoryStatus s) => s.Modified.Count() + s.Missing.Count() + s.Untracked.Count() + s.RenamedInWorkDir.Count();
}
