using System.Net.Http.Headers;
using System.Text;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Endpoints;

/// <summary>
/// Shared body capture / decoding utilities used by both <see cref="ExecuteEndpoint"/> and
/// <see cref="ExecuteStreamEndpoint"/>. Centralizing the body cap, the content-header
/// classifier, and the binary/text decoder keeps the two endpoints from drifting out of
/// sync — the streaming case used to maintain its own private copies of all three.
/// </summary>
internal static class HttpExecutionHelpers
{
    /// <summary>2 MiB cap on returned response body bytes; anything larger is truncated.</summary>
    public const int BodyCap = 2 * 1024 * 1024;

    /// <summary>Schemes the studio is willing to execute. Anything else (file://, gopher://,
    /// data://, custom app schemes, etc) is rejected before a request goes out.</summary>
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ws", "wss",
    };

    /// <summary>True when this header belongs on <see cref="HttpContent.Headers"/> rather than
    /// <see cref="HttpRequestMessage.Headers"/>. <c>HttpRequestMessage</c> rejects content
    /// headers, so we route them onto the body's <see cref="StringContent"/>.</summary>
    public static bool IsContentHeader(string name) =>
        name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Language", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Range", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase);

    /// <summary>Header name for the product token we default in when the request doesn't set one.</summary>
    private const string UserAgentHeader = "User-Agent";

    /// <summary>Stamps <c>User-Agent: tap-studio/{version}</c> onto a rendered request that
    /// doesn't already define one. Applied to the <see cref="ResolvedRequest"/> rather than to
    /// the outgoing <see cref="HttpRequestMessage"/> so the header the upstream sees is also
    /// the header the UI's Request tab reports. A request (or its collection defaults, env, or
    /// auth block) that sets its own User-Agent — including an empty one — is left untouched.</summary>
    public static ResolvedRequest WithDefaultUserAgent(ResolvedRequest rendered)
    {
        // Scanned rather than looked up: the renderer hands us an OrdinalIgnoreCase dictionary
        // today, but this shouldn't silently start double-sending the header if that changes.
        foreach (var name in rendered.Headers.Keys)
        {
            if (name.Equals(UserAgentHeader, StringComparison.OrdinalIgnoreCase)) return rendered;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in rendered.Headers) headers[k] = v;
        headers[UserAgentHeader] = StudioVersion.UserAgent;
        return rendered with { Headers = headers };
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> if the URL's scheme is outside
    /// the allow-list. Called immediately before we open a connection.</summary>
    public static void ValidateScheme(ResolvedRequest rendered)
    {
        if (!Uri.TryCreate(rendered.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Request URL '{rendered.Url}' is not an absolute URI.");
        if (!AllowedSchemes.Contains(uri.Scheme))
            throw new InvalidOperationException($"Scheme '{uri.Scheme}' is not allowed. Use http, https, ws, or wss.");
    }

    /// <summary>Decode a (possibly truncated) response body into a string. Images become data
    /// URLs so the UI can render them inline; text-like content types decode as UTF-8;
    /// everything else falls back to a length summary so a binary payload doesn't break the
    /// JSON envelope.</summary>
    public static string TryDecodeBody(byte[] bytes, string? contentType, long totalSize)
    {
        if (bytes.Length == 0) return string.Empty;

        if (contentType is not null && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var mime = contentType.Split(';')[0].Trim();
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        var isText = IsTextContentType(contentType);

        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (isText || LooksLikeText(text))
            {
                if (bytes.Length < totalSize)
                    text += $"\n\n[…truncated; {totalSize - bytes.Length:N0} more bytes follow]";
                return text;
            }
        }
        catch { /* fall through to binary summary */ }

        return $"[binary {totalSize:N0} bytes — {contentType ?? "unknown content type"}]";
    }

    public static bool IsTextContentType(string? contentType)
    {
        if (contentType is null) return false;
        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("css", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("markdown", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("csv", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeText(string s)
    {
        var sample = s.AsSpan(0, Math.Min(s.Length, 256));
        int printable = 0;
        foreach (var c in sample)
            if (c == '\n' || c == '\r' || c == '\t' || (c >= 32 && c < 127)) printable++;
        return sample.Length > 0 && printable * 100 / sample.Length >= 85;
    }

    /// <summary>Builds the outgoing <see cref="HttpRequestMessage"/> from a
    /// <see cref="ResolvedRequest"/>, routing content headers onto the body. Used by both the
    /// one-shot and streaming endpoints so header classification stays in one place.</summary>
    public static HttpRequestMessage BuildRequest(ResolvedRequest rendered)
    {
        var req = new HttpRequestMessage(new HttpMethod(rendered.Method), rendered.Url);

        // Binary file ref takes precedence over the string body: the renderer reads the
        // referenced file from disk and parks the bytes here, leaving Body holding the
        // ref text for capture/display. We ship the bytes as-is via ByteArrayContent so
        // image/PDF/whatever uploads stay byte-perfect end to end.
        HttpContent? content = null;
        if (rendered.BinaryBody is { } bytes)
        {
            content = new ByteArrayContent(bytes);
        }
        else if (rendered.Body is not null)
        {
            // RFC 2046 mandates CRLF between every line of a multipart/* body. Markdown
            // files use LF, so the body we just parsed off disk has bare \n line endings.
            // Upstream servers running ASP.NET (and any other strict parser) blow up with
            // "Line length limit 100 exceeded" because they scan past the boundary line
            // looking for \r\n. Normalize here so the disk format stays human-friendly
            // and the wire format stays spec-compliant.
            var body = IsMultipartContentType(rendered.Headers) ? NormalizeToCrlf(rendered.Body) : rendered.Body;
            content = new StringContent(body, Encoding.UTF8);
        }

        if (content is not null)
        {
            content.Headers.Clear();
            foreach (var (k, v) in rendered.Headers)
            {
                if (IsContentHeader(k))
                    content.Headers.TryAddWithoutValidation(k, v);
            }
            if (content.Headers.ContentType is null)
                content.Headers.ContentType = new MediaTypeHeaderValue(
                    rendered.BinaryBody is not null ? "application/octet-stream" : "text/plain");
            req.Content = content;
        }

        foreach (var (k, v) in rendered.Headers)
        {
            if (IsContentHeader(k)) continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        return req;
    }

    private static bool IsMultipartContentType(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
        {
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                return v.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>LF → CRLF for the entire body, preserving any \r\n pairs already present
    /// (no double-CR). Cheap one-pass scan; allocation only when a bare \n is found.</summary>
    private static string NormalizeToCrlf(string body)
    {
        if (body.Length == 0) return body;
        // Fast path: if every \n is already preceded by \r, the body is correct already.
        var needsFix = false;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\n' && (i == 0 || body[i - 1] != '\r')) { needsFix = true; break; }
        }
        if (!needsFix) return body;
        var sb = new StringBuilder(body.Length + 32);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\n' && (i == 0 || body[i - 1] != '\r')) sb.Append('\r');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Flatten an <see cref="HttpResponseMessage"/>'s headers into the same
    /// <c>Dictionary&lt;string,string&gt;</c> shape the UI consumes for <c>RequestHeaders</c>.</summary>
    public static Dictionary<string, string> FlattenHeaders(HttpResponseMessage resp)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
        return headers;
    }
}
