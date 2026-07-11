using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static readonly IReadOnlyList<AiModelOption> BuiltInModels = new[]
    {
        new AiModelOption("claude-sonnet-4.6", "Claude Sonnet 4.6"),
        new AiModelOption("claude-sonnet-4.5", "Claude Sonnet 4.5"),
        new AiModelOption("claude-haiku-4.5", "Claude Haiku 4.5"),
        new AiModelOption("claude-opus-4.6", "Claude Opus 4.6"),
        new AiModelOption("gpt-5.4", "GPT-5.4"),
        new AiModelOption("gpt-5.3-codex", "GPT-5.3 Codex"),
        new AiModelOption("gpt-5.1", "GPT-5.1"),
        new AiModelOption("gpt-5-mini", "GPT-5 Mini"),
    };

    private readonly LocatedCli _cli;
    private readonly bool _authFound;
    private static string? s_isolatedConfigDir;

    public CopilotCliProvider(string? model, string? cliPath)
    {
        Model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4.5" : model.Trim();
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
            return cli.Source is CliSource.Override or CliSource.Env
                ? $"Copilot CLI path \"{cli.Command}\" does not exist. Set the full path in AI settings or {EnvOverride}."
                : "Install the GitHub Copilot CLI (https://docs.github.com/copilot/github-copilot-cli) and sign in once. "
                  + $"Tap checks common locations and PATH; you can also set the full path in AI settings or {EnvOverride}.";
        }
        return authFound
            ? $"Copilot CLI found at {cli.Command}. Tap will spawn it for each request."
            : $"Copilot CLI found at {cli.Command}. Set GITHUB_TOKEN with Copilot access, or run the CLI once to sign in.";
    }

    // Point the CLI at an isolated config dir so it doesn't load the user's MCP servers /
    // agents on every spawn — that's where most of the startup cost lives.
    private static string IsolatedConfigDir()
    {
        if (s_isolatedConfigDir is not null) return s_isolatedConfigDir;
        var dir = Path.Combine(Path.GetTempPath(), "tap-copilot-cfg");
        try
        {
            Directory.CreateDirectory(dir);
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

    public Task<IReadOnlyList<AiModelOption>> ListModelsAsync(CancellationToken ct)
        => Task.FromResult(BuiltInModels);

    public async Task<AiChatResult> ChatAsync(AiChatInput input, CancellationToken ct)
    {
        var useModel = string.IsNullOrWhiteSpace(input.Model) ? Model : input.Model!.Trim();
        var userPrompt = FlattenConversation(input.Messages);
        var combined = $"## Instructions\n\n{input.SystemPrompt}\n\n## Request\n\n{userPrompt}";

        var args = new List<string>
        {
            "-p", combined,
            "--output-format", "json",
            "--no-color",
            "--allow-all-tools",
            "--config-dir", IsolatedConfigDir(),
        };
        if (!string.IsNullOrWhiteSpace(useModel)) { args.Add("--model"); args.Add(useModel); }

        var (code, stdout, stderr) = await CliLocator
            .RunAsync(_cli.Command, args, stdin: null, TimeSpan.FromMinutes(3), ct)
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
                cli.Source == CliSource.Fallback
                    ? "Could not find Copilot CLI in common install paths or PATH."
                    : $"Copilot CLI path \"{cli.Command}\" does not exist or is not executable.");
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
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; init; }
    [JsonPropertyName("toolName")] public string? ToolName { get; init; }
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; init; }
    [JsonPropertyName("success")] public bool? Success { get; init; }
}

[JsonSerializable(typeof(CopilotEvent))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class CopilotJson : JsonSerializerContext;
