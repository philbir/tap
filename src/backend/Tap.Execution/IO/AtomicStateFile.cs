using System.Text;

namespace Tap.Execution.IO;

/// <summary>
/// Crash-safe, owner-private writes for the JSON files Studio keeps under <c>~/.tap</c>
/// (<c>system.json</c>, <c>auth-tokens.json</c>, <c>workspaces.json</c>). Two failure modes are
/// being prevented:
///
/// <para>Truncation. <c>File.WriteAllText</c> opens the real file with <c>FileMode.Create</c>,
/// so a crash or a full disk mid-write leaves a zero-length file where a working one used to
/// be. Everything here lands in a sibling temp file and is renamed over the target, which is
/// atomic on both NTFS and POSIX filesystems.</para>
///
/// <para>Leaked secrets. The default creation mode is 0666 &amp; umask — world-readable on most
/// Linux boxes — and these files hold plaintext secret-flagged variables and OAuth refresh
/// tokens. The temp file is created 0600 up front rather than chmod'd afterwards, so the
/// content never exists on disk under a permissive mode.</para>
///
/// <para>This mirrors <c>Tap.Core.IO.AtomicFile</c>. Tap.Studio deliberately does not reference
/// Tap.Core (that assembly carries the Aspire/OIDC hosting surface), so the handful of lines is
/// duplicated rather than dragging in the dependency.</para>
/// </summary>
public static class AtomicStateFile
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/>
    /// (UTF-8, no BOM), leaving the file readable only by the current user on Unix.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

            using (var fs = new FileStream(tmp, options))
            {
                fs.Write(Encoding.UTF8.GetBytes(contents));
                // Flush to the platter before the rename: otherwise a power loss can commit
                // the directory entry while the data blocks are still in cache, which is
                // exactly the truncated-file outcome the rename is meant to prevent.
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        // The rename carries 0600 across on POSIX; re-applying costs one syscall and covers
        // filesystems that keep the destination's own mode.
        RestrictToOwner(path);
    }

    /// <summary>Creates <paramref name="path"/> and any missing parents, restricting newly
    /// created directories to the current user (0700 on Unix). Existing directories keep
    /// whatever mode they already have.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnerOnlyDirectory);
    }

    /// <summary>Best-effort tightening of an existing file to owner-only read/write (0600 on
    /// Unix). No-op on Windows, where the file already sits inside the user profile and ACLs
    /// are managed differently. Silent on failure — a permissive file beats a crashed
    /// host.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, OwnerOnlyFile);
        }
        catch
        {
            // ignore — the file is still owned by the current user
        }
    }

    /// <summary>Renames an unparseable file to <c>&lt;name&gt;.corrupt-&lt;utc&gt;</c> and
    /// returns the new path, or <c>null</c> when the move itself failed. Callers use this
    /// instead of overwriting: a file we cannot read may still hold the only copy of a
    /// secret.</summary>
    public static string? MoveAsideCorrupt(string path)
    {
        var quarantine = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
        try
        {
            File.Move(path, quarantine, overwrite: true);
            return quarantine;
        }
        catch
        {
            return null;
        }
    }
}
