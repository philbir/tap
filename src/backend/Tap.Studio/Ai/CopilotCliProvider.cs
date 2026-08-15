using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Execution.Variables;

namespace Tap.Studio.Ai;

/// <summary>
/// Drives the GitHub Copilot CLI (<c>copilot</c>) as a subprocess. We pass the prompt with
/// <c>-p</c>, request <c>--output-format json</c> (NDJSON event stream), and read the final
/// <c>assistant.message</c> event for the reply. Copilot has no <c>--system-prompt</c> flag,
/// so the system context is inlined as a leading section of the prompt — same trick mango uses.
/// </summary>
public sealed class CopilotCliProvider : IAiProvider
{
    public const string ProviderName = "copilot";
    private const string EnvOverride = "TAP_COPILOT_CLI";

    /// <summary>Last-resort catalog, used only when the live ACP probe can't run (CLI missing,
    /// signed out, offline, or too old for <c>--acp</c>). GitHub rotates its model line-up
    /// often, so treat this as a hint — <see cref="ListModelsAsync"/> prefers the live list.</summary>
    private static readonly IReadOnlyList<AiModelOption> FallbackModels = new[]
    {
        new AiModelOption("auto", "Auto"),
        new AiModelOption("claude-sonnet-5", "Claude Sonnet 5"),
        new AiModelOption("claude-opus-5", "Claude Opus 5"),
        new AiModelOption("gpt-5.3-codex", "GPT-5.3-Codex"),
        new AiModelOption("gpt-5.6-luna", "GPT-5.6 Luna"),
        new AiModelOption("gpt-5.6-sol", "GPT-5.6 Sol"),
        new AiModelOption("gpt-5.6-terra", "GPT-5.6 Terra"),
        new AiModelOption("gemini-3.1-pro-preview", "Gemini 3.1 Pro"),
    };

    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim s_modelsGate = new(1, 1);
    private static (string CliPath, DateTimeOffset FetchedAt, IReadOnlyList<AiModelOption> Models)? s_modelsCache;

    private readonly LocatedCli _cli;
    private readonly bool _authFound;
    private static string? s_isolatedConfigDir;

    public CopilotCliProvider(string? model, string? cliPath)
    {
        // Empty means "don't pass --model" — the CLI then uses the account's own default.
        // Never bake a model id in here: ids go stale and the CLI hard-fails on unknown ones.
        Model = string.IsNullOrWhiteSpace(model) ? "" : model.Trim();
        _cli = Locate(cliPath);
        _authFound = DetectAuth();
        SetupHint = BuildSetupHint(_cli, _authFound);
    }

    public string Name => ProviderName;
    public bool Configured => _cli.Found && _authFound;
    public string Model { get; }
    public string SetupHint { get; }

    internal static LocatedCli Locate(string? overridePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            "/opt/homebrew/bin/copilot",
            "/usr/local/bin/copilot",
            Path.Combine(home, ".npm-global", "bin", "copilot"),
            Path.Combine(home, ".local", "bin", "copilot"),
        };
        return CliLocator.Locate("copilot", candidates, Environment.GetEnvironmentVariable(EnvOverride), overridePath);
    }

    private static bool DetectAuth()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN"))) return true;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".copilot"),
            Path.Combine(home, ".config", "github-copilot"),
            Path.Combine(home, "Library", "Application Support", "GitHub Copilot"),
        };
        return candidates.Any(Directory.Exists);
    }

    private static string BuildSetupHint(LocatedCli cli, bool authFound)
    {
        if (!cli.Found)
        {
            if (cli.Rejection is { } rejected) return rejected;
            return cli.Source is CliSource.Override or CliSource.Env
                ? $"Copilot CLI path \"{cli.Command}\" does not exist. Set the full path in AI settings or {EnvOverride}."
                : "Install the GitHub Copilot CLI (https://docs.github.com/copilot/github-copilot-cli) and sign in once. "
                  + $"Tap checks common locations and PATH; you can also set the full path in AI settings or {EnvOverride}.";
        }
        return authFound
            ? $"Copilot CLI found at {cli.Command}. Tap will spawn it for each request."
            : $"Copilot CLI found at {cli.Command}. Set GITHUB_TOKEN with Copilot access, or run the CLI once to sign in.";
    }

    // Point the CLI at an isolated home so it doesn't load the user's MCP servers / agents /
    // skills on every spawn — that's where most of the startup cost lives. This goes in via
    // COPILOT_HOME; the older --config-dir flag is deprecated and no longer isolates MCP.
    private static string IsolatedConfigDir()
    {
        if (s_isolatedConfigDir is not null) return s_isolatedConfigDir;
        // Per-user, not shared temp: a fixed /tmp path can be pre-created by another local user,
        // who would then own the config the CLI loads. Temp is only the last resort, and the
        // 0700 below narrows that case as far as it can.
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        var dir = Path.Combine(root, "tap", "copilot-home");
        try
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { /* not ours to chmod, or a filesystem without modes — the path is still per-user */ }
            }
            var mcp = Path.Combine(dir, "mcp-config.json");
            if (!File.Exists(mcp)) File.WriteAllText(mcp, "{}\n");
        }
        catch { /* fall back to the user's default config dir */ }
        s_isolatedConfigDir = dir;
        return dir;
    }

    public async Task<IReadOnlyDictionary<string, string?>> ValidateAsync(CancellationToken ct)
    {
        var detection = await DetectAsync(_cli.Command, ct).ConfigureAwait(false);
        if (!detection.Ok) throw new InvalidOperationException(detection.Error ?? "Copilot CLI not found.");
        return new Dictionary<string, string?>
        {
            ["cliPath"] = detection.Path,
            ["cliVersion"] = detection.Version,
        };
    }

    /// <summary>Live catalog from the CLI itself, cached briefly because the ACP handshake
    /// costs a process spawn (~3s). Any failure degrades to <see cref="FallbackModels"/>
    /// rather than showing an empty dropdown.</summary>
    public async Task<IReadOnlyList<AiModelOption>> ListModelsAsync(CancellationToken ct)
    {
        if (!_cli.Found) return FallbackModels;
        if (TryReadCache() is { } fresh) return fresh;

        await s_modelsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryReadCache() is { } cached) return cached;
            var catalog = await CopilotAcp
                .ProbeAsync(_cli.Command, IsolatedConfigDir(), TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
            s_modelsCache = (_cli.Command, DateTimeOffset.UtcNow, catalog.Models);
            return catalog.Models;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FallbackModels;
        }
        finally
        {
            s_modelsGate.Release();
        }
    }

    private IReadOnlyList<AiModelOption>? TryReadCache()
        => s_modelsCache is { } c && c.CliPath == _cli.Command && DateTimeOffset.UtcNow - c.FetchedAt < ModelsCacheTtl
            ? c.Models
            : null;

    public async Task<AiChatResult> ChatAsync(AiChatInput input, CancellationToken ct)
    {
        var useModel = string.IsNullOrWhiteSpace(input.Model) ? Model : input.Model!.Trim();
        var userPrompt = FlattenConversation(input.Messages);
        var combined = $"## Instructions\n\n{input.SystemPrompt}\n\n## Request\n\n{userPrompt}";

        // The assistant's whole contract is to return a tap-request block, so it gets no tools —
        // which matters because the prompt splices in collection/variable text read off disk,
        // and a cloned workspace must not be able to talk the agent into running a shell command.
        // `--available-tools` is a filter (unlike --deny-tool, which only governs the approval
        // prompt): naming a tool that doesn't exist leaves the model with an empty tool set, so
        // there is nothing left to confirm and no need for the old --allow-all-tools.
        var args = new List<string>
        {
            "-p", combined,
            "--output-format", "json",
            "--no-color",
            "--available-tools=none",
            "--disable-builtin-mcps",
        };
        if (!string.IsNullOrWhiteSpace(useModel)) { args.Add("--model"); args.Add(useModel); }

        var env = new Dictionary<string, string> { ["COPILOT_HOME"] = IsolatedConfigDir() };
        // Empty stdin rather than null: it closes the pipe, so a CLI that ever does decide to
        // ask something gets EOF and gives up instead of blocking until our 3-minute timeout.
        var (code, stdout, stderr) = await CliLocator
            .RunAsync(_cli.Command, args, stdin: "", TimeSpan.FromMinutes(3), ct, env)
            .ConfigureAwait(false);

        string assistant = "";
        string modelOut = useModel;
        string? errored = null;
        var toolStarts = new Dictionary<string, ToolStart>(StringComparer.Ordinal);
        var toolCalls = new List<AiToolCall>();
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            CopilotEvent? evt;
            try { evt = JsonSerializer.Deserialize(trimmed, CopilotJson.Default.CopilotEvent); }
            catch { continue; }
            if (evt is null) continue;
            switch (evt.Type)
            {
                case "assistant.message" when evt.Data?.Content is { } content:
                    assistant = content;
                    // The CLI reports which model actually served the turn — authoritative when
                    // we didn't pass --model and let the account default win.
                    if (!string.IsNullOrWhiteSpace(evt.Data.Model)) modelOut = evt.Data.Model;
                    break;
                case "error" or "session.error" when evt.Data?.Message is { } msg:
                    errored = msg;
                    break;
                case "tool.execution_start" when evt.Data?.ToolName is { } name:
                    // Record start keyed by id so the matching complete can fill in success.
                    var summary = SummarizeToolArgs(name, evt.Data.Arguments);
                    var entry = new ToolStart(name, summary, toolCalls.Count);
                    toolCalls.Add(new AiToolCall(name, summary, null));
                    if (!string.IsNullOrEmpty(evt.Data.ToolCallId))
                        toolStarts[evt.Data.ToolCallId] = entry;
                    break;
                case "tool.execution_complete" when evt.Data?.ToolCallId is { } id && toolStarts.TryGetValue(id, out var started):
                    toolCalls[started.Index] = new AiToolCall(started.Name, started.Summary, evt.Data.Success);
                    break;
            }
        }

        if (errored is not null) throw new InvalidOperationException($"Copilot CLI: {errored}");
        if (code != 0 && assistant.Length == 0)
            throw new InvalidOperationException($"Copilot CLI exited with {code}: {(stderr.Trim().Length > 0 ? stderr.Trim() : "(no stderr)")}");

        return new AiChatResult(assistant, modelOut, toolCalls);
    }

    private readonly record struct ToolStart(string Name, string? Summary, int Index);

    /// <summary>Builds a short, human-readable summary of a tool call's arguments for the UI.
    /// Prefers the most descriptive field for known tools, falls back to the first scalar.</summary>
    private static string? SummarizeToolArgs(string toolName, JsonElement? args)
    {
        if (args is not { ValueKind: JsonValueKind.Object } obj) return null;

        foreach (var key in new[] { "command", "description", "query", "url", "path", "pattern", "intent", "prompt" })
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return Truncate(s!.Trim(), 160);
            }
        }
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return Truncate(s!.Trim(), 160);
            }
        }
        return null;
    }

    private static string Truncate(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string FlattenConversation(IReadOnlyList<AiChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUser is null) return "";
        if (messages.Count <= 1) return lastUser.Content;
        var history = string.Join("\n\n", messages.Select(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) ? $"User: {m.Content}" : $"Assistant: {m.Content}"));
        return $"Conversation so far:\n\n{history}\n\nReply to the latest user message.";
    }

    public static async Task<CliDetection> DetectAsync(string? overridePath, CancellationToken ct)
    {
        var cli = Locate(overridePath);
        if (!cli.Found)
        {
            return new CliDetection(false, cli.Source == CliSource.Fallback ? null : cli.Command, cli.Source, null,
                cli.Rejection ?? (cli.Source == CliSource.Fallback
                    ? "Could not find Copilot CLI in common install paths or PATH."
                    : $"Copilot CLI path \"{cli.Command}\" does not exist or is not executable."));
        }
        try
        {
            var version = await CliLocator.GetVersionAsync(cli.Command,
                line => line.StartsWith("GitHub Copilot CLI", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            return new CliDetection(true, cli.Command, cli.Source, version, null);
        }
        catch (Exception e)
        {
            return new CliDetection(false, cli.Command, cli.Source, null, e.Message);
        }
    }
}

internal sealed record CopilotEvent
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("data")] public CopilotEventData? Data { get; init; }
}

internal sealed record CopilotEventData
{
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; init; }
    [JsonPropertyName("toolName")] public string? ToolName { get; init; }
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; init; }
    [JsonPropertyName("success")] public bool? Success { get; init; }
}

[JsonSerializable(typeof(CopilotEvent))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class CopilotJson : JsonSerializerContext;
