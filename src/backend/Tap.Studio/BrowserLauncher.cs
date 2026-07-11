using System.Diagnostics;
using System.Text.Json;
using Tap.Studio.Contracts;

namespace Tap.Studio;

/// <summary>
/// Discovers the browsers (and their profiles) installed on the host and launches a URL in a
/// chosen one. Used by the OAuth "open sign-in" flow so a token can be acquired under a specific
/// browser + profile — handy when testing auth against different accounts/tenants.
///
/// Ported from mango's <c>server/src/browser.ts</c>. Runs on the machine hosting Tap.Studio
/// (the desktop sidecar or the local Aspire studio-api), which is the same box as the user's
/// browser in every supported deployment.
/// </summary>
public static class BrowserLauncher
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? ReadIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    // ---- Profile parsing -------------------------------------------------

    /// <summary>Parse a Chromium (Chrome/Edge) <c>Local State</c> file's <c>profile.info_cache</c>.</summary>
    public static IReadOnlyList<BrowserProfileDto> ParseChromiumProfiles(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("profile", out var profile)
                || !profile.TryGetProperty("info_cache", out var cache)
                || cache.ValueKind != JsonValueKind.Object)
                return [];

            var list = new List<BrowserProfileDto>();
            var index = 0;
            foreach (var entry in cache.EnumerateObject())
            {
                var key = entry.Name;
                var name = entry.Value.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
                var email = entry.Value.TryGetProperty("user_name", out var u) ? u.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(name)) name = key;
                var label = string.IsNullOrEmpty(email) ? name! : $"{name} ({email})";
                list.Add(new BrowserProfileDto(key, label, index == 0 || key == "Default"));
                index++;
            }
            return list;
        }
        catch { return []; }
    }

    /// <summary>Parse a Firefox <c>profiles.ini</c> into its named profiles.</summary>
    public static IReadOnlyList<BrowserProfileDto> ParseFirefoxProfiles(string ini)
    {
        var profiles = new List<BrowserProfileDto>();
        string? currentName = null;
        var isDefault = false;

        void Flush()
        {
            if (currentName is null) return;
            profiles.Add(new BrowserProfileDto(
                currentName,
                isDefault ? $"{currentName} (Default)" : currentName,
                isDefault));
        }

        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[Profile", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                Flush();
                currentName = null;
                isDefault = false;
                continue;
            }
            if (line.StartsWith("Name=", StringComparison.Ordinal))
            {
                var value = line["Name=".Length..].Trim();
                currentName = value.Length > 0 ? value : null;
                continue;
            }
            if (line == "Default=1") isDefault = true;
        }
        Flush();
        return profiles;
    }

    // ---- Executable + profile discovery ---------------------------------

    private static string? ResolveCommand(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains('/'))
            {
                if (File.Exists(candidate)) return candidate;
                continue;
            }
            // Bare command name — scan PATH (append .exe on Windows).
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            var names = OperatingSystem.IsWindows() ? new[] { candidate + ".exe", candidate } : new[] { candidate };
            foreach (var dir in paths)
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static IEnumerable<string> ChromiumLocalStatePaths(string browser)
    {
        if (OperatingSystem.IsMacOS())
            return [Path.Combine(Home, "Library", "Application Support",
                browser == "chrome" ? "Google/Chrome" : "Microsoft Edge", "Local State")];
        if (OperatingSystem.IsWindows())
            return [Path.Combine(Env("LOCALAPPDATA"),
                browser == "chrome" ? "Google/Chrome/User Data" : "Microsoft/Edge/User Data", "Local State")];
        if (OperatingSystem.IsLinux())
            return
            [
                Path.Combine(Home, ".config", browser == "chrome" ? "google-chrome" : "microsoft-edge", "Local State"),
                Path.Combine(Home, ".config", browser == "chrome" ? "chromium" : "microsoft-edge-beta", "Local State"),
            ];
        return [];
    }

    private static IEnumerable<string> FirefoxProfilesPaths()
    {
        if (OperatingSystem.IsMacOS())
            return [Path.Combine(Home, "Library", "Application Support", "Firefox", "profiles.ini")];
        if (OperatingSystem.IsWindows())
            return [Path.Combine(Env("APPDATA"), "Mozilla", "Firefox", "profiles.ini")];
        if (OperatingSystem.IsLinux())
            return [Path.Combine(Home, ".mozilla", "firefox", "profiles.ini")];
        return [];
    }

    private static IReadOnlyList<BrowserProfileDto> GetChromiumProfiles(string browser)
    {
        foreach (var path in ChromiumLocalStatePaths(browser))
        {
            var raw = ReadIfExists(path);
            if (raw is null) continue;
            var profiles = ParseChromiumProfiles(raw);
            if (profiles.Count > 0) return profiles;
        }
        return [];
    }

    private static IReadOnlyList<BrowserProfileDto> GetFirefoxProfiles()
    {
        foreach (var path in FirefoxProfilesPaths())
        {
            var raw = ReadIfExists(path);
            if (raw is null) continue;
            var profiles = ParseFirefoxProfiles(raw);
            if (profiles.Count > 0) return profiles;
        }
        return [];
    }

    private static string? ResolveExecutable(string browser) => browser switch
    {
        "chrome" => ResolveCommand(OperatingSystem.IsMacOS()
            ?
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
            ]
            : OperatingSystem.IsWindows()
                ?
                [
                    Path.Combine(Env("PROGRAMFILES"), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Env("PROGRAMFILES(X86)"), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Env("LOCALAPPDATA"), "Google", "Chrome", "Application", "chrome.exe"),
                ]
                : ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser"]),
        "edge" => ResolveCommand(OperatingSystem.IsMacOS()
            ?
            [
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Microsoft Edge Beta.app/Contents/MacOS/Microsoft Edge Beta",
                "/Applications/Microsoft Edge Dev.app/Contents/MacOS/Microsoft Edge Dev",
            ]
            : OperatingSystem.IsWindows()
                ?
                [
                    Path.Combine(Env("PROGRAMFILES"), "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(Env("PROGRAMFILES(X86)"), "Microsoft", "Edge", "Application", "msedge.exe"),
                ]
                : ["microsoft-edge", "microsoft-edge-stable", "microsoft-edge-beta", "microsoft-edge-dev"]),
        "firefox" => ResolveCommand(OperatingSystem.IsMacOS()
            ?
            [
                "/Applications/Firefox.app/Contents/MacOS/firefox",
                "/Applications/Firefox Developer Edition.app/Contents/MacOS/firefox",
                "/Applications/Firefox Nightly.app/Contents/MacOS/firefox",
            ]
            : OperatingSystem.IsWindows()
                ?
                [
                    Path.Combine(Env("PROGRAMFILES"), "Mozilla Firefox", "firefox.exe"),
                    Path.Combine(Env("PROGRAMFILES(X86)"), "Mozilla Firefox", "firefox.exe"),
                ]
                : ["firefox"]),
        "safari" => OperatingSystem.IsMacOS()
            ? ResolveCommand(["/Applications/Safari.app/Contents/MacOS/Safari"])
            : null,
        _ => null,
    };

    // ---- Public surface --------------------------------------------------

    public static IReadOnlyList<BrowserOptionDto> ListAvailableBrowsers() =>
    [
        new("chrome", "Google Chrome", ResolveExecutable("chrome") is not null, true, GetChromiumProfiles("chrome")),
        new("edge", "Microsoft Edge", ResolveExecutable("edge") is not null, true, GetChromiumProfiles("edge")),
        new("firefox", "Firefox", ResolveExecutable("firefox") is not null, true, GetFirefoxProfiles()),
        new("safari", "Safari", ResolveExecutable("safari") is not null, false, []),
    ];

    /// <summary>Launch <paramref name="url"/> in the chosen browser + profile, or the system
    /// default when <paramref name="browser"/> is null/empty. Throws when the browser/profile
    /// isn't available.</summary>
    public static void Open(string url, string? browser, string? profile)
    {
        if (string.IsNullOrWhiteSpace(browser))
        {
            OpenWithDefaultBrowser(url);
            return;
        }

        var option = ListAvailableBrowsers().FirstOrDefault(b => b.Id == browser)
            ?? throw new InvalidOperationException($"Unknown browser '{browser}'.");
        if (!option.Available)
            throw new InvalidOperationException($"{option.Label} is not available on this machine.");

        if (browser == "safari")
        {
            Start("open", ["-a", "Safari", url]);
            return;
        }

        var executable = ResolveExecutable(browser)
            ?? throw new InvalidOperationException($"Could not resolve the {option.Label} executable.");

        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var match = option.Profiles.FirstOrDefault(p => p.Key == profile)
                ?? throw new InvalidOperationException($"{option.Label} profile \"{profile}\" is not available.");
            if (browser == "firefox") { args.Add("-P"); args.Add(match.Key); }
            else args.Add($"--profile-directory={match.Key}");
        }
        args.Add(url);
        Start(executable, args);
    }

    private static void OpenWithDefaultBrowser(string url)
    {
        if (OperatingSystem.IsMacOS()) Start("open", [url]);
        else if (OperatingSystem.IsWindows()) Start("explorer.exe", [url]);
        else if (OperatingSystem.IsLinux()) Start("xdg-open", [url]);
        else throw new InvalidOperationException($"Unsupported platform.");
    }

    /// <summary>Fire-and-forget launch — the browser process stays alive as its own window, so we
    /// don't wait for exit (that would block). A bad executable throws from Process.Start.</summary>
    private static void Start(string command, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
