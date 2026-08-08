using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Studio.Ai;

/// <summary>
/// Drives the Claude Code CLI (<c>claude</c>) as a subprocess. Unlike Copilot it accepts a
/// real <c>--system-prompt</c>; the user prompt is piped on stdin. Output comes back as a
/// single JSON object (<c>--output-format json</c>) carrying <c>result</c> + <c>modelUsage</c>.
/// Tools are disabled (<c>--allowed-tools ""</c>) and session persistence is off so each call
/// is a clean one-shot.
/// </summary>
public sealed class ClaudeCodeProvider : IAiProvider
{
    public const string ProviderName = "claude-code";
    private const string EnvOverride = "TAP_CLAUDE_CLI";

    /// <summary>The Claude Code CLI has no model-listing command (unlike Copilot, which we
    /// probe over ACP — see <see cref="CopilotAcp"/>), so this list is maintained by hand.
    /// The aliases come first and are the safe picks: they always resolve to the current
    /// generation, so they don't rot when a pinned id is retired.</summary>
    private static readonly IReadOnlyList<AiModelOption> BuiltInModels = new[]
    {
        new AiModelOption("sonnet", "Claude Sonnet (latest)"),
        new AiModelOption("opus", "Claude Opus (latest)"),
        new AiModelOption("haiku", "Claude Haiku (latest)"),
        new AiModelOption("fable", "Claude Fable (latest)"),
        new AiModelOption("claude-sonnet-5", "Claude Sonnet 5"),
        new AiModelOption("claude-opus-5", "Claude Opus 5"),
        new AiModelOption("claude-fable-5", "Claude Fable 5"),
        new AiModelOption("claude-haiku-4-5", "Claude Haiku 4.5"),
    };

    private readonly LocatedCli _cli;
    private readonly bool _authFound;

    public ClaudeCodeProvider(string? model, string? cliPath)
    {
        Model = string.IsNullOrWhiteSpace(model) ? "sonnet" : model.Trim();
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
            Path.Combine(home, ".claude", "local", "claude"),
            Path.Combine(home, ".npm-global", "bin", "claude"),
            Path.Combine(home, ".bun", "bin", "claude"),
            Path.Combine(home, ".local", "bin", "claude"),
            "/usr/local/bin/claude",
            "/opt/homebrew/bin/claude",
        };
        return CliLocator.Locate("claude", candidates, Environment.GetEnvironmentVariable(EnvOverride), overridePath);
    }

    private static bool DetectAuth()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return true;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".config", "claude"),
            Path.Combine(home, "Library", "Application Support", "Claude"),
        };
        return candidates.Any(Directory.Exists);
    }

    private static string BuildSetupHint(LocatedCli cli, bool authFound)
    {
        if (!cli.Found)
        {
            if (cli.Rejection is { } rejected) return rejected;
            return cli.Source is CliSource.Override or CliSource.Env
                ? $"Claude Code CLI path \"{cli.Command}\" does not exist. Set the full path in AI settings or {EnvOverride}."
                : "Install the Claude Code CLI (https://claude.com/claude-code) and sign in once. "
                  + $"Tap checks common locations and PATH; you can also set the full path in AI settings or {EnvOverride}.";
        }
        return authFound
            ? $"Claude Code CLI found at {cli.Command}. Tap will spawn it for each request."
            : $"Claude Code CLI found at {cli.Command}. Set ANTHROPIC_API_KEY, or run the CLI once to sign in.";
    }

    public async Task<IReadOnlyDictionary<string, string?>> ValidateAsync(CancellationToken ct)
    {
        var detection = await DetectAsync(_cli.Command, ct).ConfigureAwait(false);
        if (!detection.Ok) throw new InvalidOperationException(detection.Error ?? "Claude Code CLI not found.");
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

        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--system-prompt", input.SystemPrompt,
            "--allowed-tools", "",
            "--no-session-persistence",
        };
        if (!string.IsNullOrWhiteSpace(useModel)) { args.Add("--model"); args.Add(useModel); }

        var (code, stdout, stderr) = await CliLocator
            .RunAsync(_cli.Command, args, stdin: userPrompt, TimeSpan.FromMinutes(3), ct)
            .ConfigureAwait(false);

        if (code != 0 && stdout.Trim().Length == 0)
            throw new InvalidOperationException($"Claude CLI exited with {code}: {(stderr.Trim().Length > 0 ? stderr.Trim() : "(no stderr)")}");

        ClaudeResult? parsed;
        try { parsed = JsonSerializer.Deserialize(stdout.Trim(), ClaudeJson.Default.ClaudeResult); }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Claude CLI output was not valid JSON: {e.Message}. First 200 chars: {stdout[..Math.Min(200, stdout.Length)]}");
        }

        if (parsed is null) throw new InvalidOperationException("Claude CLI returned no result.");
        if (parsed.IsError) throw new InvalidOperationException($"Claude CLI reported an error: {parsed.Result ?? "(no detail)"}");

        var text = parsed.Result ?? "";
        var modelOut = parsed.ModelUsage is { Count: > 0 } ? parsed.ModelUsage.Keys.First() : useModel;
        return new AiChatResult(text, modelOut, []);
    }

    private static string FlattenConversation(IReadOnlyList<AiChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUser is null) return "";
        if (messages.Count <= 1) return lastUser.Content;
        var history = string.Join("\n\n", messages.Select(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) ? $"User: {m.Content}" : $"Assistant: {m.Content}"));
        return $"Conversation so far:\n\n{history}\n\nReply to the latest user message in markdown.";
    }

    public static async Task<CliDetection> DetectAsync(string? overridePath, CancellationToken ct)
    {
        var cli = Locate(overridePath);
        if (!cli.Found)
        {
            return new CliDetection(false, cli.Source == CliSource.Fallback ? null : cli.Command, cli.Source, null,
                cli.Rejection ?? (cli.Source == CliSource.Fallback
                    ? "Could not find Claude Code CLI in common install paths or PATH."
                    : $"Claude Code CLI path \"{cli.Command}\" does not exist or is not executable."));
        }
        try
        {
            var version = await CliLocator.GetVersionAsync(cli.Command,
                line => line.Contains("claude", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            return new CliDetection(true, cli.Command, cli.Source, version, null);
        }
        catch (Exception e)
        {
            return new CliDetection(false, cli.Command, cli.Source, null, e.Message);
        }
    }
}

internal sealed record ClaudeResult
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("is_error")] public bool IsError { get; init; }
    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("modelUsage")] public Dictionary<string, JsonElement>? ModelUsage { get; init; }
}

[JsonSerializable(typeof(ClaudeResult))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ClaudeJson : JsonSerializerContext;
