using System.Text.Json;
using Tap.Execution.Contracts;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Agent;

/// <summary>
/// The one JSON dialect every agent-facing surface speaks — CLI <c>--json</c>, the stdio MCP
/// tools, the Studio's <c>/mcp</c> endpoint. camelCase to match what the Studio API emits,
/// and deliberately the default encoder: <see cref="SecretRedactor"/> computes its
/// JSON-escaped variants with that same encoder, so a different one here would open a gap
/// between how a secret is escaped in the payload and what the redactor looks for.
/// </summary>
public static class AgentJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializes <paramref name="payload"/>, scrubbed through
    /// <paramref name="redactor"/> when one is supplied. Redaction runs on the serialized
    /// text so it also catches a secret inside any nested string — a URL query, a response
    /// body preview, an assertion's actual value.</summary>
    public static string Serialize<T>(T payload, SecretRedactor? redactor = null)
    {
        var json = JsonSerializer.Serialize(payload, Options);
        return redactor is null ? json : redactor.Redact(json)!;
    }

    /// <summary>The run-report envelope: totals over the runs, then the engine's own result
    /// shape per target. Always an envelope, even for one target, so no reader has to branch
    /// on how many were selected.</summary>
    public static string TestReport(IReadOnlyList<TestRunResultDto> runs, SecretRedactor? redactor = null)
        => Serialize(
            new TestReportEnvelope(
                Ok: runs.All(r => r.Ok),
                Passed: runs.Sum(r => r.Passed),
                Failed: runs.Sum(r => r.Failed),
                Skipped: runs.Sum(r => r.Skipped),
                DurationMs: runs.Sum(r => r.DurationMs),
                Runs: runs),
            redactor);

    private sealed record TestReportEnvelope(
        bool Ok, int Passed, int Failed, int Skipped, double DurationMs,
        IReadOnlyList<TestRunResultDto> Runs);
}
