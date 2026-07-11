using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Studio.Auth;

/// <summary>
/// Persisted store of OAuth tokens obtained for auth profiles. Keyed by
/// <c>{workspaceRoot}::{authRelativePath}</c> so multiple workspaces don't collide.
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

    public AuthTokenEntry? Get(string workspaceRoot, string authPath)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(Key(workspaceRoot, authPath));
        }
    }

    public void Save(string workspaceRoot, string authPath, AuthTokenEntry entry)
    {
        lock (_gate)
        {
            _entries[Key(workspaceRoot, authPath)] = entry;
            Persist();
        }
    }

    public void Remove(string workspaceRoot, string authPath)
    {
        lock (_gate)
        {
            _entries.Remove(Key(workspaceRoot, authPath));
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

    private static string Key(string workspaceRoot, string authPath)
        => $"{workspaceRoot}::{authPath}";
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
