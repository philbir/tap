using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Studio.Auth;

/// <summary>
/// Persisted store of OAuth tokens obtained for auth profiles. Keyed by
/// <c>{workspaceRoot}::{authRelativePath}</c> so multiple workspaces don't collide, with a
/// <c>#{stage}</c> suffix for profiles owned by a collection that defines stages and an
/// <c>@{envPath}</c> suffix for the environment in effect. The same profile resolved under
/// <c>dev</c> and <c>prod</c> — whether those are stages or envs — points at different token
/// endpoints and must not share an entry.
///
/// Tokens are sensitive — this file lives under the user's state folder
/// (<c>~/.tap/auth-tokens.json</c>), not in the workspace itself (which is checked into Git).
/// Re-running the auth flow overwrites the entry; explicit logout removes it.
/// </summary>
public sealed class AuthTokenStore
{
    private readonly string _path;
    private readonly ILogger<AuthTokenStore> _logger;
    private readonly Lock _gate = new();
    private Dictionary<string, AuthTokenEntry> _entries;
    private DateTime _loadedWriteTimeUtc;

    public AuthTokenStore(StudioOptions options, ILogger<AuthTokenStore> logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(options.StatePath) ?? Path.GetTempPath();
        _path = Path.Combine(dir, "auth-tokens.json");

        var (outcome, loaded) = TryLoad(_path);
        if (outcome is LoadOutcome.Corrupt)
        {
            // Refresh tokens are not reproducible from anything else on disk. Move the
            // unreadable file aside instead of letting the next Save() overwrite it.
            var quarantined = AtomicStateFile.MoveAsideCorrupt(_path);
            if (quarantined is not null)
                _logger.LogError("Could not parse '{Path}'. Moved it to '{Quarantine}'; every profile must sign in again.", _path, quarantined);
            else
                _logger.LogError("Could not parse '{Path}' and could not move it aside. Every profile must sign in again.", _path);
        }

        _entries = loaded ?? new Dictionary<string, AuthTokenEntry>();
        _loadedWriteTimeUtc = SafeWriteTimeUtc(_path);
    }

    /// <summary>Re-reads <c>auth-tokens.json</c> when its on-disk timestamp moved past what we
    /// loaded. The packaged desktop app and a dev instance routinely share <c>~/.tap</c>, and
    /// <see cref="Save"/> runs on every silent refresh, not just on interactive sign-in —
    /// without this, the second process re-serializes its startup snapshot and quietly drops
    /// every token the first one obtained. Must be called under <see cref="_gate"/>.</summary>
    private void ReloadIfChangedLocked()
    {
        var mtime = SafeWriteTimeUtc(_path);
        if (mtime == _loadedWriteTimeUtc) return;
        var (outcome, fresh) = TryLoad(_path);
        if (outcome is LoadOutcome.Corrupt)
        {
            // Keep the in-memory copy rather than adopting garbage. Advance the watermark so
            // this warns once per on-disk change, not once per token read.
            _loadedWriteTimeUtc = mtime;
            _logger.LogWarning("'{Path}' changed on disk but could not be parsed; keeping the in-memory tokens.", _path);
            return;
        }
        if (fresh is not null)
        {
            _entries = fresh;
            _loadedWriteTimeUtc = mtime;
        }
    }

    private static DateTime SafeWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public AuthTokenEntry? Get(string workspaceRoot, AuthProfileScope scope)
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            return _entries.GetValueOrDefault(Key(workspaceRoot, scope));
        }
    }

    public void Save(string workspaceRoot, AuthProfileScope scope, AuthTokenEntry entry)
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            _entries[Key(workspaceRoot, scope)] = entry;
            Persist();
        }
    }

    /// <summary>Drop every cached token for <paramref name="authPath"/> — the bare entry plus
    /// one per stage/env combination. "Clear token" in the UI means the profile is signed out,
    /// not just signed out of whichever stage and env happen to be selected.</summary>
    public void RemoveAll(string workspaceRoot, string authPath)
    {
        lock (_gate)
        {
            ReloadIfChangedLocked();
            // Escaping makes the path segment free of both separators, so the base key is a
            // prefix of every variant and of nothing else.
            var baseKey = Key(workspaceRoot, new AuthProfileScope(authPath, null, null));
            var doomed = _entries.Keys
                .Where(k => k == baseKey
                    || k.StartsWith(baseKey + StageSeparator, StringComparison.Ordinal)
                    || k.StartsWith(baseKey + EnvSeparator, StringComparison.Ordinal))
                .ToArray();
            if (doomed.Length == 0) return;
            foreach (var k in doomed) _entries.Remove(k);
            Persist();
        }
    }

    private enum LoadOutcome { Missing, Loaded, Corrupt }

    /// <summary>Reads the token file, keeping "not written yet" and "there but unreadable"
    /// apart so the caller can quarantine the second case instead of overwriting it.</summary>
    private static (LoadOutcome Outcome, Dictionary<string, AuthTokenEntry>? Entries) TryLoad(string path)
    {
        if (!File.Exists(path)) return (LoadOutcome.Missing, null);
        try
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize(json, AuthTokenJson.Default.DictionaryStringAuthTokenEntry);
            if (entries is null) return (LoadOutcome.Corrupt, null);
            return (LoadOutcome.Loaded, entries);
        }
        catch
        {
            // Never crash the server over a bad token file — the caller logs and the user can
            // always re-authenticate.
            return (LoadOutcome.Corrupt, null);
        }
    }

    private void Persist()
    {
        AtomicStateFile.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicStateFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, AuthTokenJson.Default.DictionaryStringAuthTokenEntry));
        _loadedWriteTimeUtc = SafeWriteTimeUtc(_path);
    }

    private const string StageSeparator = "#";
    private const string EnvSeparator = "@";

    private static string Key(string workspaceRoot, AuthProfileScope scope)
    {
        var key = $"{workspaceRoot}::{Escape(scope.Path)}";
        if (scope.Stage is { Length: > 0 } stage) key += StageSeparator + Escape(stage);
        if (scope.Env is { Length: > 0 } env) key += EnvSeparator + Escape(env);
        return key;
    }

    /// <summary>Backslash-escape the separators inside a segment so the composed key is
    /// injective. Both <c>#</c> and <c>@</c> are legal in a file name, so without this a
    /// profile at <c>a@b.auth.md</c> and a profile at <c>a</c> under env <c>b.auth.md</c>
    /// would key identically — and <see cref="RemoveAll"/>'s prefix scan would delete across
    /// profiles.</summary>
    private static string Escape(string segment)
        => segment
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace(StageSeparator, @"\" + StageSeparator, StringComparison.Ordinal)
            .Replace(EnvSeparator, @"\" + EnvSeparator, StringComparison.Ordinal);
}

public sealed record AuthTokenEntry
{
    public required string AccessToken { get; init; }
    public string? IdToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? TokenType { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required DateTimeOffset ObtainedAt { get; init; }
    /// <summary>Token endpoint that issued this — used for refresh.</summary>
    public string? TokenEndpoint { get; init; }
    public string? ClientId { get; init; }
    public IReadOnlyList<string>? Scopes { get; init; }
}

[JsonSerializable(typeof(Dictionary<string, AuthTokenEntry>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AuthTokenJson : JsonSerializerContext
{
}
