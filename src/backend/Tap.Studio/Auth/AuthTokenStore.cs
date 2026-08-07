using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Studio.Auth;

/// <summary>
/// Persisted store of OAuth tokens obtained for auth profiles. Keyed by
/// <c>{workspaceRoot}::{authRelativePath}</c> so multiple workspaces don't collide, with a
/// <c>#{stage}</c> suffix for profiles owned by a collection that defines stages — the same
/// profile resolved under <c>dev</c> and <c>prod</c> points at different token endpoints and
/// must not share an entry. Workspace-scoped profiles key exactly as they always have.
///
/// Tokens are sensitive — this file lives under the user's state folder
/// (<c>~/.tap/auth-tokens.json</c>), not in the workspace itself (which is checked into Git).
/// Re-running the auth flow overwrites the entry; explicit logout removes it.
/// </summary>
public sealed class AuthTokenStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AuthTokenEntry> _entries;

    public AuthTokenStore(StudioOptions options)
    {
        var dir = Path.GetDirectoryName(options.StatePath) ?? Path.GetTempPath();
        _path = Path.Combine(dir, "auth-tokens.json");
        _entries = Load();
    }

    public AuthTokenEntry? Get(string workspaceRoot, AuthProfileScope scope)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(Key(workspaceRoot, scope));
        }
    }

    public void Save(string workspaceRoot, AuthProfileScope scope, AuthTokenEntry entry)
    {
        lock (_gate)
        {
            _entries[Key(workspaceRoot, scope)] = entry;
            Persist();
        }
    }

    /// <summary>Drop every cached token for <paramref name="authPath"/> — the un-staged entry
    /// plus one per stage. "Clear token" in the UI means the profile is signed out, not just
    /// signed out of whichever stage happens to be selected.</summary>
    public void RemoveAll(string workspaceRoot, string authPath)
    {
        lock (_gate)
        {
            var baseKey = Key(workspaceRoot, new AuthProfileScope(authPath, null));
            var stagePrefix = baseKey + StageSeparator;
            var doomed = _entries.Keys
                .Where(k => k == baseKey || k.StartsWith(stagePrefix, StringComparison.Ordinal))
                .ToArray();
            if (doomed.Length == 0) return;
            foreach (var k in doomed) _entries.Remove(k);
            Persist();
        }
    }

    private Dictionary<string, AuthTokenEntry> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, AuthTokenJson.Default.DictionaryStringAuthTokenEntry)
                   ?? new Dictionary<string, AuthTokenEntry>();
        }
        catch
        {
            // Corrupt or unreadable — start fresh rather than crashing the server. The user
            // can always re-authenticate.
            return new();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, AuthTokenJson.Default.DictionaryStringAuthTokenEntry));
        TryRestrictPermissions(_path);
    }

    /// <summary>
    /// Best-effort tightening of the token file to user-only read/write (0600 on Unix).
    /// Silent on Windows — NTFS ACLs are managed differently and the system-dir is already
    /// inside the user profile. Silent on failure: a wide-open token file is preferable to a
    /// crashed Studio process; the warning in <see cref="StudioHost"/> covers misconfigured
    /// machines well enough.
    /// </summary>
    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // ignore — the file is still owned by the current user
        }
    }

    private const string StageSeparator = "#";

    private static string Key(string workspaceRoot, AuthProfileScope scope)
        => scope.Stage is { Length: > 0 } stage
            ? $"{workspaceRoot}::{scope.Path}{StageSeparator}{stage}"
            : $"{workspaceRoot}::{scope.Path}";
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
