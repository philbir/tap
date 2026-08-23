using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Security;

namespace Tap.Studio.History;

/// <summary>
/// Reads and writes <c>.tap-history/</c> — the workspace-local record of exchanges that have
/// actually been run.
///
/// <para><b>Layout.</b> One folder per request id, one file per exchange, named with a UTC
/// timestamp so a plain directory listing is already in chronological order:
/// <c>.tap-history/&lt;request-id&gt;/20260822T093918.742Z-0a1b2c3d.json</c>. There is
/// deliberately no index file. An index would have to be kept in step with the files it
/// describes across crashes, concurrent Studios, and a user deleting things by hand — and the
/// only thing it would buy is avoiding a directory enumeration that takes single-digit
/// milliseconds. Filenames carry the ordering; the files are the truth.</para>
///
/// <para><b>Secrets.</b> An entry is either redacted and plaintext, or unredacted and encrypted
/// (<c>.json.enc</c>, the same AES-256-GCM envelope the file variable provider uses). There is
/// no third combination, and the encrypt path fails closed rather than falling back to
/// plaintext — see <see cref="TryWrite"/>.</para>
///
/// <para>The folder writes its own <c>.gitignore</c> on first use, so history stays out of Git
/// without anyone having to remember. That matters most in exactly the case where forgetting
/// would hurt: encrypted entries hold real credentials.</para>
/// </summary>
public sealed class HistoryStore(string rootDirectory, IEncryptionKeySource keySource)
{
    /// <summary>Folder name under the workspace root. Not <c>.history</c>, which the VS Code
    /// Local History extension already claims — two tools writing snapshots into one directory
    /// is a diagnosis nobody should have to make.</summary>
    public const string DirectoryName = ".tap-history";

    private const string PlainExtension = ".json";
    private const string EncryptedExtension = ".json.enc";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Per-workspace salt: one machine key, a key of its own for this workspace's history.
    private readonly DerivedKey _key = new(keySource, "tap-history:" + Fingerprint(rootDirectory));

    public string Root { get; } = Path.Combine(rootDirectory, DirectoryName);

    /// <summary>True when this machine can decrypt (or produce) encrypted entries.</summary>
    public bool HasKey => keySource.GetPassphrase() is not null;

    // -- Writing ---------------------------------------------------------------------------

    /// <summary>
    /// Stores one entry and prunes the request's folder back to <paramref name="options"/>'s
    /// <c>maxEntries</c>. Returns the file written, or null when nothing was.
    ///
    /// <para>Recording is a side effect of a request the user actually cared about, so nothing
    /// here is allowed to turn a successful send into a failure: every I/O and crypto fault is
    /// caught and reported through <paramref name="problem"/> for the caller to log.</para>
    ///
    /// <para>The one case that refuses rather than degrades is <c>encrypt: true</c> on a machine
    /// where no key can be obtained. Encryption is also what licenses storing unredacted
    /// secrets, so silently writing the plaintext instead would spill credentials to disk in the
    /// exact configuration that asked for the opposite.</para>
    /// </summary>
    public string? TryWrite(HistoryEntry entry, HistoryOptions options, out string? problem)
    {
        problem = null;
        try
        {
            byte[]? key = null;
            if (options.EffectiveEncrypt)
            {
                key = _key.Get(create: true);
                if (key is null)
                {
                    problem = "history.encrypt is on but this machine has no encryption key and one "
                        + $"could not be created — set {MachineEncryptionKeySource.EnvVar} or run "
                        + "`tap-studio key init`. Nothing was recorded.";
                    return null;
                }
            }

            var folder = EnsureFolder(entry.RequestId);
            var json = JsonSerializer.Serialize(entry, Json);
            var path = Path.Combine(folder, entry.Id + (key is null ? PlainExtension : EncryptedExtension));

            File.WriteAllText(path, key is null ? json : SecretEnvelope.Protect(json, key));
            Prune(folder, options.EffectiveMaxEntries);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>An entry id: sortable timestamp plus enough randomness that two sends in the
    /// same millisecond can't collide.</summary>
    public static string NewEntryId(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss.fff'Z'") + "-" + Guid.NewGuid().ToString("n")[..8];

    // -- Reading ---------------------------------------------------------------------------

    /// <summary>Summaries for one request, newest first.</summary>
    public IReadOnlyList<HistorySummary> ListForRequest(string requestId, LoadedWorkspace workspace, int limit = 100)
    {
        var folder = Path.Combine(Root, Safe(requestId));
        if (!Directory.Exists(folder)) return [];
        var files = Directory.EnumerateFiles(folder)
            .Where(IsEntryFile)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Take(limit);
        return [.. files.Select(f => Summarize(f, workspace)).OfType<HistorySummary>()];
    }

    /// <summary>
    /// The newest entries across every request. Enumerates filenames only — no file is opened
    /// until the top <paramref name="limit"/> have been picked, which is what keeps a timeline
    /// over a workspace with thousands of recorded exchanges instant.
    /// </summary>
    public IReadOnlyList<HistorySummary> ListRecent(LoadedWorkspace workspace, int limit = 100)
    {
        if (!Directory.Exists(Root)) return [];
        var newest = Directory.EnumerateDirectories(Root)
            .SelectMany(d => Directory.EnumerateFiles(d).Where(IsEntryFile))
            // The timestamp prefix makes the filename the sort key across folders too.
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Take(limit);
        return [.. newest.Select(f => Summarize(f, workspace)).OfType<HistorySummary>()];
    }

    /// <summary>One entry in full, or null when it is gone or unreadable.</summary>
    public HistoryEntry? Read(string requestId, string entryId)
    {
        var path = FindEntryFile(requestId, entryId);
        return path is null ? null : ReadFile(path);
    }

    /// <summary>True when the entry exists but is encrypted and this machine cannot read it.</summary>
    public bool IsLocked(string requestId, string entryId)
    {
        var path = FindEntryFile(requestId, entryId);
        return path is not null && IsEncrypted(path) && _key.Get() is null;
    }

    // -- Deleting --------------------------------------------------------------------------

    public bool DeleteEntry(string requestId, string entryId)
    {
        var path = FindEntryFile(requestId, entryId);
        if (path is null) return false;
        TryDelete(path);
        return true;
    }

    public bool DeleteRequest(string requestId)
    {
        var folder = Path.Combine(Root, Safe(requestId));
        if (!Directory.Exists(folder)) return false;
        try { Directory.Delete(folder, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Drops the history of requests that no longer exist. <paramref name="olderThanDays"/> of 0
    /// sweeps every orphan on sight; anything higher gives a deleted-then-restored request (a
    /// reverted commit, a branch switch) a window in which its history is still waiting for it.
    /// </summary>
    public int SweepOrphans(LoadedWorkspace workspace, int olderThanDays)
    {
        if (!Directory.Exists(Root)) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, olderThanDays));
        var swept = 0;
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var requestId = Path.GetFileName(folder);
            if (workspace.FindById(requestId) is not null) continue;
            try
            {
                var newest = Directory.EnumerateFiles(folder).Where(IsEntryFile)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                if (newest > cutoff) continue;
                Directory.Delete(folder, recursive: true);
                swept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return swept;
    }

    /// <summary>Drops every orphaned folder regardless of age — the "clear these now" action.</summary>
    public int DeleteOrphans(LoadedWorkspace workspace) => SweepOrphans(workspace, olderThanDays: 0);

    // -- Internals -------------------------------------------------------------------------

    private string EnsureFolder(string requestId)
    {
        Directory.CreateDirectory(Root);
        EnsureGitignore();
        var folder = Path.Combine(Root, Safe(requestId));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Writes <c>.tap-history/.gitignore</c> containing <c>*</c>, so the folder excludes itself
    /// — including this file. Recorded traffic is not source, and with <c>encrypt: true</c> it
    /// is real credentials; neither belongs in a commit because somebody forgot a line in the
    /// repo's own ignore file.
    /// </summary>
    private void EnsureGitignore()
    {
        var path = Path.Combine(Root, ".gitignore");
        if (File.Exists(path)) return;
        try { File.WriteAllText(path, "# Recorded request history — never source.\n*\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes oldest-first until at most <paramref name="keep"/> remain. Cheap: one
    /// listing of a folder that this very call is keeping bounded.</summary>
    private static void Prune(string folder, int keep)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(IsEntryFile)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        for (var i = 0; i < files.Length - keep; i++) TryDelete(files[i]);
    }

    private string? FindEntryFile(string requestId, string entryId)
    {
        var folder = Path.Combine(Root, Safe(requestId));
        if (!Directory.Exists(folder)) return null;
        var stem = Safe(entryId);
        foreach (var extension in new[] { PlainExtension, EncryptedExtension })
        {
            var path = Path.Combine(folder, stem + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private HistoryEntry? ReadFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (IsEncrypted(path))
            {
                var key = _key.Get();
                if (key is null) return null;
                text = SecretEnvelope.Unprotect(text, key);
            }
            var entry = JsonSerializer.Deserialize<HistoryEntry>(text, Json);
            // A document from a newer Tap is skipped rather than half-read: a reader that
            // guesses at a shape it doesn't know produces a plausible-looking lie.
            return entry is null || entry.V > 1 ? null : entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private HistorySummary? Summarize(string path, LoadedWorkspace workspace)
    {
        var encrypted = IsEncrypted(path);
        var entry = ReadFile(path);
        var requestId = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        var orphaned = workspace.FindById(requestId) is null;

        if (entry is null)
        {
            // Unreadable. If it is encrypted and we have no key, that is a state worth showing —
            // "there are twelve entries here you can't open" beats an empty list. Anything else
            // (corrupt, from the future) is simply skipped.
            if (!encrypted || _key.Get() is not null) return null;
            var stem = EntryStem(path);
            return new HistorySummary(
                Id: stem, RequestId: requestId, At: ParseAt(stem),
                RequestPath: null, RequestName: null, Collection: null, Env: null,
                Method: "—", Url: string.Empty, Status: null, StatusText: null,
                DurationMs: 0, BodyBytes: 0, Ok: false, AssertSummary: null, Error: null,
                Encrypted: true, Locked: true, Orphaned: orphaned);
        }

        var ok = entry.Error is null
            && entry.Response is { Status: >= 200 and < 400 }
            && entry.AssertSummary?.Ok != false;

        return new HistorySummary(
            Id: entry.Id,
            RequestId: entry.RequestId,
            At: entry.At,
            RequestPath: entry.RequestPath,
            RequestName: entry.RequestName,
            Collection: entry.Collection,
            Env: entry.Env,
            Method: entry.Request.Method,
            Url: entry.Request.Url,
            Status: entry.Response?.Status,
            StatusText: entry.Response?.StatusText,
            DurationMs: entry.DurationMs,
            BodyBytes: entry.Response?.BodyBytes ?? 0,
            Ok: ok,
            AssertSummary: entry.AssertSummary,
            Error: entry.Error,
            Encrypted: encrypted,
            Locked: false,
            Orphaned: orphaned);
    }

    private static bool IsEntryFile(string path)
        => path.EndsWith(PlainExtension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsEncrypted(string path)
        => path.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase);

    private static string EntryStem(string path)
    {
        var name = Path.GetFileName(path);
        return IsEncrypted(path) ? name[..^EncryptedExtension.Length] : name[..^PlainExtension.Length];
    }

    /// <summary>Best-effort timestamp from an entry stem, for rows we couldn't open. The
    /// filename is the only thing an encrypted entry tells us without a key.</summary>
    private static DateTimeOffset ParseAt(string stem)
        => DateTimeOffset.TryParseExact(
            stem.Split('-')[0], "yyyyMMdd'T'HHmmss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var at) ? at : DateTimeOffset.MinValue;

    /// <summary>Ids come from workspace files and route parameters, so they are strangers here.
    /// Anything that isn't a plain id character is dropped rather than escaped — a folder name
    /// is not the place to be clever about traversal.</summary>
    private static string Safe(string id)
        => new([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')]);

    /// <summary>Short stable fingerprint of the workspace path, used as the PBKDF2 salt suffix so
    /// two workspaces on one machine derive different keys.</summary>
    private static string Fingerprint(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
