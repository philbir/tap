using System.Security.Cryptography;
using System.Text;

namespace Tap.Workspace.Security;

/// <summary>
/// The machine's encryption passphrase — one secret, resolved once, used everywhere Tap
/// stores data that must survive at rest without being readable by anyone who gets the file.
/// Today that is the <c>file</c> variable provider's store; the abstraction exists because
/// the next thing that needs at-rest encryption must not invent a second key.
///
/// <para>Two sources, in order:</para>
/// <list type="number">
///   <item><c>TAP_ENCRYPTION_KEY</c> in the process environment. Wins outright — this is how
///     CI supplies the key, and an explicitly exported variable should never be silently
///     overruled by a file left on the box.</item>
///   <item><c>&lt;system-dir&gt;/encryption.key</c> (<c>$TAP_SYSTEM_DIR</c>, else <c>~/.tap</c>),
///     a single-line file written owner-only.</item>
/// </list>
///
/// <para>Never the workspace. A passphrase committed beside the ciphertext it unlocks is not
/// encryption, and there is no configuration path here that would allow it.</para>
/// </summary>
public interface IEncryptionKeySource
{
    /// <summary>The passphrase, or <c>null</c> when this machine has no key configured.
    /// Read-only: never creates one. This is the call for status, diagnostics, and every
    /// <i>decrypt</i> — a missing key there means the data is unreadable, and answering that
    /// with a freshly minted key would turn an actionable error into silent corruption.</summary>
    string? GetPassphrase();

    /// <summary>The passphrase, creating one on this machine if it has none. This is the call
    /// for the <i>encrypt</i> path: the first secret Tap is asked to store is the moment a key
    /// is actually needed, so that is when it appears — no <c>key init</c> step in between.
    /// Still <c>null</c> when no key exists and none can be created (a host that supplies its
    /// own key, or a home directory that cannot be written).</summary>
    string? EnsurePassphrase();

    /// <summary>Where the passphrase came from, for diagnostics the user can act on.</summary>
    EncryptionKeyOrigin Origin { get; }
}

public enum EncryptionKeyOrigin
{
    /// <summary>No key on this machine.</summary>
    None,
    /// <summary><c>TAP_ENCRYPTION_KEY</c>.</summary>
    Environment,
    /// <summary><c>&lt;system-dir&gt;/encryption.key</c>.</summary>
    KeyFile,
}

/// <summary>Default <see cref="IEncryptionKeySource"/>: environment variable, then key file.
/// Resolution is per-call rather than cached — the Studio is long-lived, and a user who runs
/// "generate a key" in Settings expects the very next save to encrypt.</summary>
public sealed class MachineEncryptionKeySource(string? systemDir = null) : IEncryptionKeySource
{
    /// <summary>Environment variable holding the passphrase. Replaces the pre-0.7.0
    /// <c>TAP_FILE_PROVIDER_KEY</c> (and its per-provider <c>_&lt;NAME&gt;</c> form), which no
    /// longer has any effect — the key stopped being a property of one provider.</summary>
    public const string EnvVar = "TAP_ENCRYPTION_KEY";

    /// <summary>File name under the system directory.</summary>
    public const string FileName = "encryption.key";

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Shared instance for the common case (no explicit system dir).</summary>
    public static MachineEncryptionKeySource Default { get; } = new();

    /// <summary>The directory holding <c>encryption.key</c>: <c>$TAP_SYSTEM_DIR</c>, else
    /// <c>~/.tap</c>. Same resolution the settings store applies, so one <c>TAP_SYSTEM_DIR</c>
    /// moves every piece of user-level state together.</summary>
    public static string DefaultSystemDir()
    {
        var configured = Environment.GetEnvironmentVariable("TAP_SYSTEM_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tap");
    }

    /// <summary>Absolute path of the key file this source reads and writes.</summary>
    public string KeyFilePath => Path.Combine(systemDir ?? DefaultSystemDir(), FileName);

    public EncryptionKeyOrigin Origin
    {
        get
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar))) return EncryptionKeyOrigin.Environment;
            return ReadKeyFile() is not null ? EncryptionKeyOrigin.KeyFile : EncryptionKeyOrigin.None;
        }
    }

    public string? GetPassphrase()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        return ReadKeyFile();
    }

    /// <summary>
    /// Resolves the passphrase, generating <see cref="KeyFilePath"/> when this machine has
    /// none. Deliberately *not* what <see cref="GetPassphrase"/> does — see the interface.
    ///
    /// <para>An exported <see cref="EnvVar"/> short-circuits it: the variable already answers
    /// the question, and writing a key file nothing reads is the same mistake
    /// <c>key init</c> refuses to make.</para>
    /// </summary>
    public string? EnsurePassphrase()
    {
        if (GetPassphrase() is { } existing) return existing;

        try
        {
            return GenerateKeyFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Either another process won the race to create the file — in which case its key
            // is now *the* key and ours was never written — or the directory is not writable.
            // Re-reading answers both: the winner's passphrase, or null for the caller to
            // report with the usual both-sources hint.
            return ReadKeyFile();
        }
    }

    private string? ReadKeyFile()
    {
        var path = KeyFilePath;
        try
        {
            if (!File.Exists(path)) return null;
            // One line, trimmed: a trailing newline from `echo … > encryption.key` must not
            // become part of the passphrase, or a hand-written key stops matching a
            // UI-written one and every secret reads as corrupt.
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes a freshly generated 32-byte random passphrase to the key file and
    /// returns it. Refuses to overwrite an existing key unless <paramref name="force"/> — the
    /// old key is the only thing that can read data already encrypted with it, so replacing it
    /// silently is indistinguishable from destroying that data.</summary>
    public string GenerateKeyFile(bool force = false)
    {
        var path = KeyFilePath;
        if (!force && ReadKeyFile() is not null) throw AlreadyHoldsKey(path);

        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, OwnerOnlyDirectory);

        // CreateNew, not Create: the check above is a check, and a check has a window. Two
        // processes reaching EnsurePassphrase() at once would both pass it, and the second
        // would truncate the first's key — leaving whatever the first had already encrypted
        // unreadable. Letting the filesystem arbitrate closes that window; the loser is told
        // the same thing it would have been told a millisecond earlier.
        //
        // A file that exists but holds nothing (`touch encryption.key`) is not a key and has
        // nothing to lose, so it stays overwritable — otherwise it would wedge the machine
        // into a state where no key can ever be generated.
        var options = new FileStreamOptions
        {
            Mode = force || KeyFileIsBlank() ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

        try
        {
            using var fs = new FileStream(path, options);
            fs.Write(Encoding.UTF8.GetBytes(passphrase + "\n"));
            fs.Flush(flushToDisk: true);
        }
        catch (IOException) when (!force && File.Exists(path))
        {
            throw AlreadyHoldsKey(path);
        }

        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, OwnerOnlyFile); }
            catch { /* still owned by the current user */ }
        }
        return passphrase;
    }

    /// <summary>True when the key file is present but empty — readable, and holding no key.
    /// Distinct from "unreadable", which must never be treated as clobberable.</summary>
    private bool KeyFileIsBlank()
    {
        try
        {
            return File.Exists(KeyFilePath) && File.ReadAllText(KeyFilePath).Trim().Length == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static InvalidOperationException AlreadyHoldsKey(string path) => new(
        $"'{path}' already holds a key. Move it aside first if you really mean to replace it — "
        + "data encrypted with the old key will not decrypt with the new one.");
}

/// <summary>Fixed-passphrase source for tests and for hosts that resolve the key their own
/// way (a hosted Studio pulling it from its platform's secret store, say).</summary>
public sealed class StaticEncryptionKeySource(string? passphrase) : IEncryptionKeySource
{
    public string? GetPassphrase() => string.IsNullOrEmpty(passphrase) ? null : passphrase;

    /// <summary>Nothing to create — this source's key is whatever the host handed it.</summary>
    public string? EnsurePassphrase() => GetPassphrase();

    public EncryptionKeyOrigin Origin => string.IsNullOrEmpty(passphrase) ? EncryptionKeyOrigin.None : EncryptionKeyOrigin.Environment;
}
