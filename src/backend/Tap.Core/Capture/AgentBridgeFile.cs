using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Core.IO;

namespace Tap.Core.Capture;

/// <summary>How a bridge finds a running inspector, and proves it is allowed to read it.</summary>
/// <param name="Token">Random per inspector run. Held in memory by the server and on disk in a
/// file only its owner can read.</param>
public sealed record AgentBridgeHandle(
    [property: JsonPropertyName("uiPort")] int UiPort,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AgentBridgeHandle))]
internal sealed partial class AgentBridgeJson : JsonSerializerContext;

/// <summary>
/// The handshake between a running inspector and a bridge that wants to read its traffic.
///
/// <para>Why a file rather than a flag the user pastes: the file's permissions <em>are</em> the
/// authorization. It is written <c>0600</c>, so a process running as the same user can read it
/// and nothing else can — which is exactly the boundary that matters here. Loopback is not one:
/// <c>ProfileEndpoints</c> already says so in this codebase, and it is just as true for captured
/// traffic as for tunnel credentials. Any other local process could otherwise read every request
/// your app has served.</para>
///
/// <para>The token lives for one inspector run and the file is removed on shutdown, so a stale
/// file cannot authorize anything against a later process — the token would not match.</para>
/// </summary>
public static class AgentBridgeFile
{
    /// <summary>The header a bridge presents. Deliberately not <c>Authorization</c>: the
    /// inspector's own proxy auth may already be using that, and two unrelated credentials in
    /// one header is how confused-deputy bugs start.</summary>
    public const string HeaderName = "X-Tap-Agent-Token";

    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tap", "inspector");

    public static string PathFor(int uiPort, string? rootOverride = null)
        => Path.Combine(rootOverride ?? DefaultRootDirectory, $"{uiPort}.json");

    /// <summary>A fresh bridge token. Split from <see cref="Write"/> so a host can hold the
    /// token from startup while publishing the file only once its port is actually bound —
    /// a handle for a port you failed to bind points a bridge at somebody else's server.</summary>
    public static string MintToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Writes the handle and returns it. Overwrites any handle left by a previous run
    /// on this port — that one is dead by definition.</summary>
    public static AgentBridgeHandle Write(int uiPort, string? token = null, string? rootOverride = null)
    {
        var handle = new AgentBridgeHandle(
            UiPort: uiPort,
            Url: $"http://localhost:{uiPort}",
            Token: token ?? MintToken(),
            Pid: Environment.ProcessId,
            StartedAt: DateTimeOffset.UtcNow);

        var root = rootOverride ?? DefaultRootDirectory;
        AtomicFile.CreateDirectory(root);
        AtomicFile.WriteAllText(
            PathFor(uiPort, rootOverride),
            JsonSerializer.Serialize(handle, AgentBridgeJson.Default.AgentBridgeHandle));

        return handle;
    }

    public static AgentBridgeHandle? Read(int uiPort, string? rootOverride = null)
        => ReadFile(PathFor(uiPort, rootOverride));

    /// <summary>
    /// The most recently started inspector that left a handle, so <c>tap mcp</c> works with no
    /// arguments in the common case of exactly one running. Returns null when there is nothing
    /// to find; ambiguity is resolved by recency rather than by refusing, because a developer
    /// with two inspectors up almost always means the one they just started.
    /// </summary>
    public static AgentBridgeHandle? Discover(string? rootOverride = null)
    {
        var root = rootOverride ?? DefaultRootDirectory;
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateFiles(root, "*.json")
            .Select(ReadFile)
            .Where(h => h is not null)
            .OrderByDescending(h => h!.StartedAt)
            .FirstOrDefault();
    }

    public static void Delete(int uiPort, string? rootOverride = null)
    {
        try
        {
            File.Delete(PathFor(uiPort, rootOverride));
        }
        catch (IOException)
        {
            // Shutdown cleanup: a handle we could not remove is inert anyway, since its token
            // dies with the process that minted it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static AgentBridgeHandle? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var handle = JsonSerializer.Deserialize(
                File.ReadAllText(path), AgentBridgeJson.Default.AgentBridgeHandle);

            // A handle whose process is gone is worse than no handle: the port may since have
            // been taken by an unrelated server, and following it would send this token to
            // something that never issued it. Crashes and kill -9 both skip the cleanup hook,
            // so this is the common case, not the exotic one.
            return handle is not null && IsAlive(handle.Pid) ? handle : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
