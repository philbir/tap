using System.Text.Json;
using Tap.Core.IO;

namespace Tap.Core.Profiles;

/// <summary>
/// File-backed store for named tunnel profiles. One JSON file per profile under
/// <c>~/.tap/tunnels</c> (all platforms; <c>~</c> resolves via the user-profile folder).
///
/// <para>Profiles carry live credentials — the Cloudflare API token, the connector token, and
/// the OIDC client secret — so both the directory and the files are created owner-only.</para>
/// </summary>
public sealed class TunnelProfileStore
{
    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tap", "tunnels");

    public string RootDirectory { get; }

    public TunnelProfileStore(string? rootOverride = null)
    {
        RootDirectory = rootOverride ?? DefaultRootDirectory;
        AtomicFile.CreateDirectory(RootDirectory);
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
            // Tolerate stale file with different "name" — trust the filename. WithName copies
            // every field; the previous hand-written projection dropped all five tailscale*
            // properties, so an ephemeral-mode profile lost its auth key on every load.
            return p.WithName(name);
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
        AtomicFile.CreateDirectory(RootDirectory);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(profile, TunnelProfileJson.Default.TunnelProfile));
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
