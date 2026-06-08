using LibGit2Sharp;

namespace Tap.Studio;

/// <summary>
/// Inspects a directory for an enclosing git repository and reports current branch + remote
/// info. Pure read-only — no fetches, no writes. Used by the workspace switcher / editor to
/// surface "this workspace lives in &lt;branch&gt; of &lt;remote&gt;" without the user
/// dropping into a terminal.
///
/// <para>The walk starts at the requested path and climbs the directory tree looking for a
/// <c>.git</c> directory (or file, for worktrees). Returning the discovered git root means
/// a workspace stored at <c>~/code/myrepo/.tap</c> can still show parent-repo info.</para>
/// </summary>
public sealed class GitInspector
{
    public static string? FindGitRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

        // Repository.Discover returns the path to the `.git/` folder (or worktree marker).
        // We want the working-directory root, which is the parent.
        var discovered = Repository.Discover(path);
        if (string.IsNullOrEmpty(discovered)) return null;

        var trimmed = discovered.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // `.git` directory case: parent is the working-dir root. Worktrees and bare repos
        // are edge cases we don't currently surface; treat them as "no info".
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? null : Path.GetFullPath(parent);
    }

    public static GitInfo? Inspect(string path)
    {
        var root = FindGitRoot(path);
        if (root is null) return null;
        try
        {
            using var repo = new Repository(root);
            var head = repo.Head;
            var branch = head.FriendlyName;
            var remotes = repo.Network.Remotes
                .Select(r => new GitRemoteInfo(r.Name, r.Url))
                .ToArray();
            var origin = remotes.FirstOrDefault(r => r.Name == "origin")
                ?? remotes.FirstOrDefault();
            return new GitInfo(
                Root: root,
                Branch: branch,
                IsDetached: head.IsTracking is false && head.Tip is not null && !head.IsCurrentRepositoryHead && head.CanonicalName?.StartsWith("refs/heads/") is false,
                OriginUrl: origin?.Url,
                Remotes: remotes);
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
    }
}

public sealed record GitInfo(
    string Root,
    string Branch,
    bool IsDetached,
    string? OriginUrl,
    IReadOnlyList<GitRemoteInfo> Remotes);

public sealed record GitRemoteInfo(string Name, string Url);
