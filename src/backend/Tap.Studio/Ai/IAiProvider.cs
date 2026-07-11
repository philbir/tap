namespace Tap.Studio.Ai;

/// <summary>One selectable model exposed by a provider.</summary>
public sealed record AiModelOption(string Id, string? Name = null);

/// <summary>A single turn in the assistant conversation. <c>Role</c> is <c>user</c> or <c>assistant</c>.</summary>
public sealed record AiChatMessage(string Role, string Content);

/// <summary>Input to <see cref="IAiProvider.ChatAsync"/>: a system prompt plus the running
/// conversation. <c>Model</c> overrides the provider default when set.</summary>
public sealed record AiChatInput(string SystemPrompt, IReadOnlyList<AiChatMessage> Messages, string? Model);

/// <summary>Result of a chat call — the assistant reply text, the model that produced it,
/// and any tool calls the CLI ran along the way (empty when the provider runs without tools).</summary>
public sealed record AiChatResult(string Text, string Model, IReadOnlyList<AiToolCall> ToolCalls);

/// <summary>A single tool invocation surfaced from the CLI's event stream, shown in the UI so
/// the user can see what the assistant did (e.g. ran a shell command, fetched a URL).</summary>
public sealed record AiToolCall(string Name, string? Summary, bool? Success);

/// <summary>
/// Abstraction over a locally-installed AI coding CLI (GitHub Copilot CLI or Claude Code).
/// We spawn the user's installed binary per request instead of bundling an SDK — the same
/// approach mango uses — so Tap.Studio carries no per-platform native dependencies and the
/// user's existing CLI auth is reused as-is.
/// </summary>
public interface IAiProvider
{
    /// <summary>Stable provider id: <c>copilot</c> or <c>claude-code</c>.</summary>
    string Name { get; }

    /// <summary>True when both the CLI binary and an auth signal were found — i.e. a chat
    /// call has a reasonable chance of succeeding without further setup.</summary>
    bool Configured { get; }

    /// <summary>The default model this provider will use when a request doesn't pick one.</summary>
    string Model { get; }

    /// <summary>Human-readable next step shown in the UI when something needs configuring.</summary>
    string SetupHint { get; }

    /// <summary>Cheap liveness probe — runs the CLI's <c>--version</c> and returns diagnostics.
    /// Throws when the CLI can't be found or run.</summary>
    Task<IReadOnlyDictionary<string, string?>> ValidateAsync(CancellationToken ct);

    /// <summary>Send the conversation to the CLI and return its reply.</summary>
    Task<AiChatResult> ChatAsync(AiChatInput input, CancellationToken ct);

    /// <summary>List the models the user can pick. Best-effort — falls back to a built-in catalog.</summary>
    Task<IReadOnlyList<AiModelOption>> ListModelsAsync(CancellationToken ct);
}
