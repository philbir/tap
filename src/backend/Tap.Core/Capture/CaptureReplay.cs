using System.Text.Json.Serialization;

namespace Tap.Core.Capture;

/// <summary>
/// A request to send a captured exchange again, optionally with edits.
///
/// <para>The edits are deliberately narrow. A replay re-sends the <em>captured</em> headers,
/// including whatever credential the original carried — which is the point, since it lets an
/// agent reproduce an authenticated request without ever holding the credential. That same
/// property makes an unconstrained destination an exfiltration channel: an agent that could
/// choose the target would be choosing where to send somebody else's token.</para>
///
/// <para>So <see cref="Path"/> stays relative and the host is not editable. This is the same
/// reasoning that keeps Studio's dynamic requests inside their collection's baseUrl.</para>
/// </summary>
public sealed record CaptureReplayRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("contentType")] string? ContentType = null,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers = null)
{
    /// <summary>Header names an edit may never set, because each of them redirects where the
    /// captured credential goes.</summary>
    public static readonly string[] UneditableHeaders = ["Host", "X-Forwarded-Host", "X-Forwarded-For", "Forwarded"];

    /// <summary>
    /// Why this replay must be refused, or null when it is safe to send. Checked before
    /// anything leaves the process — a request that would escape the original host is not
    /// sent and then reported, it is never sent.
    /// </summary>
    public string? Rejection()
    {
        if (Path is { } path)
        {
            if (!path.StartsWith('/'))
            {
                return "path must be relative and start with '/'. A replay re-sends the captured " +
                    "credential, so it stays on the host it was captured from.";
            }

            if (path.StartsWith("//", StringComparison.Ordinal) ||
                path.Contains("://", StringComparison.Ordinal))
            {
                return "path must not contain a scheme or authority — that would redirect the " +
                    "captured credential to another host.";
            }
        }

        foreach (var name in Headers?.Keys ?? Enumerable.Empty<string>())
        {
            if (UneditableHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return $"'{name}' cannot be edited on a replay: it decides where the captured " +
                    "credential is sent.";
            }
        }

        return null;
    }
}

/// <summary>
/// What came of a replay. <paramref name="CapturedId"/> is the replay's own record — the
/// replay goes back through the proxy, so it is captured like any other request and can be
/// read straight back with describe_request.
/// </summary>
public sealed record CaptureReplayEnvelope(
    [property: JsonPropertyName("trust")] string Trust,
    [property: JsonPropertyName("replayed")] bool Replayed,
    [property: JsonPropertyName("status")] int? Status,
    [property: JsonPropertyName("capturedId")] string? CapturedId,
    [property: JsonPropertyName("error")] string? Error)
{
    public static CaptureReplayEnvelope Sent(int status, string? capturedId)
        => new(CaptureTrust.Notice, true, status, capturedId, null);

    public static CaptureReplayEnvelope Refused(string why)
        => new(CaptureTrust.Notice, false, null, null, why);
}
