using Tap.Workspace.Variables;

namespace Tap.Studio;

/// <summary>
/// Configuration consumed by <see cref="StudioHost"/>. Read from <c>Studio:*</c> keys in
/// <see cref="IConfiguration"/>; the AppHost will write them via <c>WithEnvironment</c>.
/// </summary>
public sealed class StudioOptions
{
    /// <summary>HTTP port the REST API + UI listen on.</summary>
    public required int Port { get; init; }

    public string Host { get; init; } = "localhost";

    /// <summary>Absolute path to the workspace root (the folder containing <c>.tap/</c>).</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>
    /// Path to the local SQLite DB for history / UI state. Lives under the user profile, not in
    /// the workspace folder. Created on first run.
    /// </summary>
    public required string StatePath { get; init; }

    /// <summary>
    /// Absolute path to the directory that holds the user-level Tap configuration —
    /// <c>system.json</c> (system-scope variable providers + system variables), the known
    /// workspaces list, and the state DB. Controlled by the <c>TAP_SYSTEM_DIR</c> env var;
    /// defaults to <c>~/.tap</c>. Created on first access.
    /// </summary>
    public required string SystemDir { get; init; }

    /// <summary>System-scope variable providers parsed from <c>Studio:VariableProviders:[]</c>
    /// in appsettings. Used only as the initial seed for <c>system.json</c>: once the file
    /// exists, it is the source of truth and these entries are ignored. Kept on
    /// <see cref="StudioOptions"/> so a host can ship default providers in its config.</summary>
    public required IReadOnlyList<VariableProviderConfig> SystemProviders { get; init; }

    public static StudioOptions FromConfiguration(IConfiguration config)
    {
        var port = config.GetValue<int?>("Studio:Port") ?? 5298;
        var host = config["Studio:Host"] ?? "localhost";
        var workspace = config["Studio:WorkspaceRoot"]
            ?? Environment.CurrentDirectory;
        var systemDir = Environment.GetEnvironmentVariable("TAP_SYSTEM_DIR");
        if (string.IsNullOrWhiteSpace(systemDir))
        {
            systemDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".tap");
        }
        systemDir = Path.GetFullPath(systemDir);

        var state = config["Studio:StatePath"]
            ?? Path.Combine(systemDir, "state.db");

        return new StudioOptions
        {
            Port = port,
            Host = host,
            WorkspaceRoot = Path.GetFullPath(workspace),
            StatePath = Path.GetFullPath(state),
            SystemDir = systemDir,
            SystemProviders = ReadSystemProviders(config),
        };
    }

    /// <summary>
    /// Reads <c>Studio:Providers:[]</c>. Each entry has the same shape as a workspace
    /// provider declaration: <c>name</c>, <c>type</c>, <c>mode</c> (default <c>read</c>),
    /// and a <c>settings</c> sub-object. Entries missing <c>name</c> or <c>type</c> are
    /// ignored — silent-skip beats fail-on-startup for an optional feature.
    /// </summary>
    private static IReadOnlyList<VariableProviderConfig> ReadSystemProviders(IConfiguration config)
    {
        var list = new List<VariableProviderConfig>();
        foreach (var section in config.GetSection("Studio:VariableProviders").GetChildren())
        {
            var name = section["name"];
            var type = section["type"];
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type)) continue;

            var settings = new Dictionary<string, string?>();
            foreach (var kv in section.GetSection("settings").GetChildren())
            {
                settings[kv.Key] = kv.Value;
            }

            list.Add(new VariableProviderConfig
            {
                Name = name,
                Type = type,
                Settings = settings,
                Origin = ProviderOrigin.System,
            });
        }
        return list;
    }
}
