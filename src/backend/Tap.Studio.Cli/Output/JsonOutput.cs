using System.Text.Json;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Cli.Output;

/// <summary>
/// Machine output for the <c>--json</c> modes. Two rules, both about being parseable:
///
/// <para>It writes to the real stdout, not through Spectre — Spectre wraps text at the
/// console width, and a hard line break in the middle of a JSON string is a broken
/// document. And it stays on the default <see cref="JsonSerializerOptions.Encoder"/>,
/// because <see cref="SecretRedactor"/> computes its JSON-escaped variants with that same
/// encoder; switching to a relaxed encoder here would open a gap between how a secret is
/// escaped in the payload and what the redactor looks for.</para>
/// </summary>
public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializes <paramref name="payload"/> to stdout, scrubbed through
    /// <paramref name="redactor"/> when one is supplied. Redaction happens on the serialized
    /// text so it also catches a secret inside any nested string — a URL query, a response
    /// body preview, an assertion's actual value.</summary>
    public static void Write<T>(T payload, SecretRedactor? redactor = null)
    {
        var json = JsonSerializer.Serialize(payload, Options);
        Console.Out.WriteLine(redactor is null ? json : redactor.Redact(json));
    }
}
