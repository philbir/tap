using System.Text;

namespace Tap.Core.IO;

/// <summary>
/// Crash-safe, owner-private writes for the small JSON files Tap keeps under the user profile
/// (tunnel profiles, cloudflared credentials, Studio state). Two failure modes are being
/// prevented:
///
/// <para>Truncation. <c>File.WriteAllText</c> opens the real file with <c>FileMode.Create</c>,
/// so a crash — or a full disk — mid-write leaves a zero-length or half-written file where a
/// working one used to be. Everything here lands in a sibling temp file first and is renamed
/// over the target, which is atomic on both NTFS and POSIX filesystems.</para>
///
/// <para>Leaked secrets. The default creation mode is 0666 &amp; umask, i.e. world-readable on
/// most Linux boxes. The temp file is created 0600 up front rather than chmod'd afterwards, so
/// there is never an instant where the content exists on disk under a permissive mode.</para>
/// </summary>
public static class AtomicFile
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/>
    /// (UTF-8, no BOM), leaving the file readable only by the current user on Unix.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = TempPathFor(path);
        try
        {
            using (var fs = CreateRestricted(tmp))
            {
                fs.Write(Encoding.UTF8.GetBytes(contents));
                // Flush all the way to the platter before the rename: without it a power loss
                // can commit the directory entry while the data blocks are still in cache,
                // which is exactly the truncated-file outcome the rename is meant to avoid.
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        // The rename carries the temp file's 0600 across on POSIX; re-applying costs one
        // syscall and covers filesystems that preserve the destination's own mode.
        RestrictToOwner(path);
    }

    /// <inheritdoc cref="WriteAllText(string, string)"/>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        var tmp = TempPathFor(path);
        try
        {
            await using (var fs = CreateRestricted(tmp))
            {
                await fs.WriteAsync(Encoding.UTF8.GetBytes(contents), ct);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        RestrictToOwner(path);
    }

    /// <summary>Creates <paramref name="path"/> and any missing parents, restricting newly
    /// created directories to the current user (0700 on Unix). No-op for directories that
    /// already exist — an existing mode is the user's business.</summary>
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
    /// instead of overwriting: a config file we cannot read may still be the only copy of a
    /// secret the user has.</summary>
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

    private static FileStream CreateRestricted(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
        return new FileStream(path, options);
    }

    // Process-scoped temp name so two Studio instances writing the same file race on the
    // rename (harmless — last writer wins) rather than on an exclusively-held temp handle.
    private static string TempPathFor(string path) => $"{path}.{Environment.ProcessId}.tmp";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
