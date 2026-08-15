namespace Tap.Workspace.Model;

/// <summary>
/// Wire protocol declared by a <see cref="RequestFile"/>. Distinct from the HTTP method,
/// because a WebSocket connection happens over an HTTP upgrade but the wire semantics
/// after the handshake are not request/response. Drives both the renderer's baseUrl
/// scheme normalization (http→ws, https→wss) and the executor's transport choice.
/// </summary>
public enum RequestProtocol
{
    /// <summary>Plain HTTP request/response. Default when no <c>protocol:</c> frontmatter is set.</summary>
    Http,
    /// <summary>WebSocket — the URL is opened with a <c>ws://</c>/<c>wss://</c> scheme and
    /// the executor pumps frames in both directions instead of reading a response body.</summary>
    WebSocket,
}

public static class RequestProtocolExtensions
{
    public static string ToWire(this RequestProtocol p) => p switch
    {
        RequestProtocol.WebSocket => "websocket",
        _ => "http",
    };

    /// <summary>Case-insensitive parse. Accepts <c>http</c>, <c>websocket</c>, and the alias
    /// <c>ws</c>. Returns <c>null</c> for an unknown value so the caller can decide whether
    /// to raise <c>E_UNKNOWN_FIELD</c>.</summary>
    public static RequestProtocol? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "http" or "https" => RequestProtocol.Http,
            "ws" or "wss" or "websocket" => RequestProtocol.WebSocket,
            _ => null,
        };
    }
}
