using System.Text.Json;

namespace Tap.Core.Profiles;

/// <summary>
/// File-backed store for named tunnel profiles. One JSON file per profile under
/// <c>~/.tap/tunnels</c> (all platforms; <c>~</c> resolves via the user-profile folder).
/// </summary>
public sealed class TunnelProfileStore
{
    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tap", "tunnels");

    public string RootDirectory { get; }

    public TunnelProfileStore(string? rootOverride = null)
    {
        RootDirectory = rootOverride ?? DefaultRootDirectory;
        Directory.CreateDirectory(RootDirectory);
    }

    public IReadOnlyList<string> ListNames()
        => Directory.EnumerateFiles(RootDirectory, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<TunnelProfile> ListAll()
        => ListNames().Select(Load).Where(p => p is not null).Select(p => p!).ToArray();

    public TunnelProfile? Load(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            var p = JsonSerializer.Deserialize(fs, TunnelProfileJson.Default.TunnelProfile);
            if (p is null) return null;
            // Tolerate stale file with different "name" — trust the filename.
            return new TunnelProfile
            {
                Name = name,
                Upstream = p.Upstream,
                ProxyPort = p.ProxyPort,
                UiPort = p.UiPort,
                TunnelMode = p.TunnelMode,
                Token = p.Token,
                ApiToken = p.ApiToken,
                AccountId = p.AccountId,
                ApiManagedTunnelName = p.ApiManagedTunnelName,
                DynamicZone = p.DynamicZone,
                Hostname = p.Hostname,
                Docker = p.Docker,
                AutoInstall = p.AutoInstall,
                AuthHeader = p.AuthHeader,
                AuthCidrs = p.AuthCidrs,
                AuthCountries = p.AuthCountries,
                OidcAuthority = p.OidcAuthority,
                OidcClientId = p.OidcClientId,
                OidcClientSecret = p.OidcClientSecret,
            };
        }
        catch
        {
            return null;
        }
    }

    public void Save(TunnelProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));
        var path = ResolvePath(profile.Name);
        Directory.CreateDirectory(RootDirectory);
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, profile, TunnelProfileJson.Default.TunnelProfile);
    }

    public bool Delete(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public string ResolveFilePath(string name) => ResolvePath(name);

    private string ResolvePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            throw new ArgumentException($"Invalid profile name '{name}'.", nameof(name));
        return Path.Combine(RootDirectory, $"{name}.json");
    }
}
