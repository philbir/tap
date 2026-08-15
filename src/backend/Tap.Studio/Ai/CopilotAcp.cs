using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Studio.Ai;

/// <summary>The live model catalog reported by the Copilot CLI, plus the model it would
/// pick on its own when no <c>--model</c> is passed.</summary>
internal sealed record AcpModelCatalog(IReadOnlyList<AiModelOption> Models, string? DefaultModelId);

/// <summary>
/// Asks the Copilot CLI for its model catalog over ACP (Agent Client Protocol).
/// <c>copilot --acp</c> speaks line-delimited JSON-RPC on stdio, and the reply to
/// <c>session/new</c> carries <c>models.availableModels</c> — the same list the interactive
/// <c>/model</c> picker shows, already resolved against the signed-in account's entitlements.
/// There is no <c>copilot models</c> subcommand and no documented HTTP endpoint we can reach
/// without minting a Copilot token ourselves, so this is the only first-party way to enumerate
/// them. Hardcoding the list goes stale every time GitHub rotates its model line-up.
/// </summary>
internal static class CopilotAcp
{
    private const int NewSessionId = 2;

    // Both requests go out up front: the agent answers them in order, so we don't need to
    // round-trip on `initialize` before asking for a session.
    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false}}}}""";

    public static async Task<AcpModelCatalog> ProbeAsync(
        string cliPath, string configHome, TimeSpan timeout, CancellationToken ct)
    {
        var cwd = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        psi.ArgumentList.Add("--acp");
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["COPILOT_HOME"] = configHome;

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not spawn \"{cliPath}\" in ACP mode: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        // Drain stderr so a chatty CLI can't fill the pipe buffer and wedge itself.
        _ = Task.Run(async () =>
        {
            try { await proc.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* process went away — nothing to drain */ }
        }, CancellationToken.None);

        try
        {
            await proc.StandardInput.WriteLineAsync(InitializeRequest.AsMemory(), token).ConfigureAwait(false);
            await proc.StandardInput.WriteLineAsync(NewSessionRequest(cwd).AsMemory(), token).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(token).ConfigureAwait(false);
            // stdin deliberately stays open: closing it ends the ACP session before it answers.

            while (await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                AcpMessage? msg;
                try { msg = JsonSerializer.Deserialize(line, CopilotAcpJson.Default.AcpMessage); }
                catch { continue; }
                if (msg?.Id != NewSessionId) continue;

                if (msg.Error is { } err)
                    throw new InvalidOperationException($"Copilot ACP rejected session/new: {err.Message ?? "(no detail)"}");
                if (msg.Result?.Models?.AvailableModels is not { Count: > 0 } available)
                    throw new InvalidOperationException("Copilot ACP returned no models.");

                var models = available
                    .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
                    .Select(m => new AiModelOption(m.ModelId!, string.IsNullOrWhiteSpace(m.Name) ? m.ModelId! : m.Name!))
                    .ToArray();
                return new AcpModelCatalog(models, msg.Result.Models.CurrentModelId);
            }

            throw new InvalidOperationException("Copilot ACP closed the stream without answering session/new.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Copilot ACP model probe timed out after {timeout.TotalSeconds:0}s.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    private static string NewSessionRequest(string cwd)
        => "{\"jsonrpc\":\"2.0\",\"id\":" + NewSessionId
           + ",\"method\":\"session/new\",\"params\":{\"cwd\":\"" + JsonEncodedText.Encode(cwd)
           + "\",\"mcpServers\":[]}}";
}

internal sealed record AcpMessage
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("result")] public AcpSessionResult? Result { get; init; }
    [JsonPropertyName("error")] public AcpErrorInfo? Error { get; init; }
}

internal sealed record AcpSessionResult
{
    [JsonPropertyName("models")] public AcpModelState? Models { get; init; }
}

internal sealed record AcpModelState
{
    [JsonPropertyName("availableModels")] public List<AcpModelInfo>? AvailableModels { get; init; }
    [JsonPropertyName("currentModelId")] public string? CurrentModelId { get; init; }
}

internal sealed record AcpModelInfo
{
    [JsonPropertyName("modelId")] public string? ModelId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

internal sealed record AcpErrorInfo
{
    [JsonPropertyName("message")] public string? Message { get; init; }
}

[JsonSerializable(typeof(AcpMessage))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class CopilotAcpJson : JsonSerializerContext;
