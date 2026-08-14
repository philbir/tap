using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Http;

/// <summary>
/// Shared body capture / decoding utilities and the outbound redirect policy, used by
/// <see cref="ExecuteEndpoint"/>, <see cref="ExecuteStreamEndpoint"/> and
/// <see cref="GraphQLSchemaEndpoint"/>. Centralizing the body cap, the content-header
/// classifier, and the binary/text decoder keeps the endpoints from drifting out of
/// sync — the streaming case used to maintain its own private copies of all three.
/// </summary>
public static class HttpExecutionHelpers
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
        headers[UserAgentHeader] = ExecutionIdentity.UserAgent;
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

    /// <summary>Read at most <see cref="BodyCap"/> bytes of a response body and decode them as
    /// UTF-8. For callers that only want text and have no use for the true byte count — a
    /// GraphQL introspection reply that doesn't fit in 2 MiB isn't a schema we could hand the
    /// UI anyway. Requires the response to have been obtained with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, otherwise the body is already
    /// fully buffered and the cap buys nothing.</summary>
    public static async Task<string> ReadCappedStringAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (ms.Length < BodyCap)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;
            var slack = BodyCap - (int)ms.Length;
            ms.Write(buf, 0, Math.Min(read, slack));
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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

    /// <summary>Redirect hops we follow before giving up. Matches the <see cref="HttpClientHandler"/>
    /// default — the cap exists to bound a redirect loop, not to second-guess the upstream.</summary>
    public const int MaxRedirects = 10;

    /// <summary>Networks an <em>automatic</em> hop may never land on. Loopback (127/8 and
    /// 0.0.0.0/8, which the stack treats as "this host"), link-local — which is where the
    /// 169.254.169.254 cloud metadata endpoint lives — and IPv6 unique-local. Aiming a request
    /// at these yourself is ordinary local development and stays allowed; being *steered* into
    /// them by a remote <c>Location</c> header is how a workspace request turns into a read
    /// primitive against the machine running the studio.</summary>
    private static readonly (IPNetwork Network, string Label)[] BlockedRedirectNetworks =
    [
        (IPNetwork.Parse("127.0.0.0/8"), "loopback"),
        (IPNetwork.Parse("0.0.0.0/8"), "loopback"),
        (IPNetwork.Parse("::1/128"), "loopback"),
        (IPNetwork.Parse("169.254.0.0/16"), "link-local"),
        (IPNetwork.Parse("fe80::/10"), "link-local"),
        // fc00::/7 is the ULA block; only fd00::/8 is actually assigned, but the upper half is
        // unallocated so refusing the whole /7 costs nothing.
        (IPNetwork.Parse("fc00::/7"), "IPv6 unique-local"),
    ];

    /// <summary>RFC1918 space. Deliberately <em>not</em> blocked by default: pointing tap at a
    /// service on the LAN is a legitimate use of the tool. Set
    /// <c>TAP_STUDIO_BLOCK_PRIVATE_REDIRECTS=1</c> to refuse redirects into it as well — for
    /// installs where the studio can reach internal hosts a workspace author shouldn't.</summary>
    private static readonly (IPNetwork Network, string Label)[] PrivateRedirectNetworks =
    [
        (IPNetwork.Parse("10.0.0.0/8"), "private network"),
        (IPNetwork.Parse("172.16.0.0/12"), "private network"),
        (IPNetwork.Parse("192.168.0.0/16"), "private network"),
    ];

    private static readonly bool BlockPrivateRedirects =
        Environment.GetEnvironmentVariable("TAP_STUDIO_BLOCK_PRIVATE_REDIRECTS") is { } flag
        && (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The only headers that survive a redirect onto a different host.
    /// <see cref="HttpClient"/> strips <c>Authorization</c> across hosts but forwards everything
    /// else verbatim, so an <c>X-API-Key</c> rendered out of Key Vault or 1Password would ride a
    /// 302 to wherever the upstream pointed. Nothing carrying a secret is on this list.</summary>
    private static readonly HashSet<string> CrossHostSafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Charset", "Accept-Encoding", "Accept-Language",
        "Cache-Control", "Pragma", "User-Agent",
    };

    /// <summary>
    /// Sends <paramref name="request"/> and follows redirects by hand. Every handler in the
    /// studio runs with <c>AllowAutoRedirect = false</c> so each hop after the first can be
    /// re-checked: the URL the user typed is theirs to choose — <c>http://localhost:3000</c> and
    /// LAN addresses are the normal case — but a <c>Location</c> chosen by a remote server is
    /// not, and must not be able to walk the request into loopback or link-local space still
    /// carrying the workspace's credential headers.
    /// </summary>
    /// <remarks>The caller keeps ownership of <paramref name="request"/> and of the returned
    /// response; intermediate hops are disposed here.</remarks>
    public static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var origin = request.RequestUri
            ?? throw new InvalidOperationException("Request has no URI.");

        // Buffered up front because an HttpContent is single-use: a 307/308 replay needs its own
        // copy of the body and of the headers that described it.
        byte[]? body = null;
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            contentHeaders = [.. request.Content.Headers];
        }

        var current = request;
        var hops = 0;
        while (true)
        {
            var resp = await client.SendAsync(current, completionOption, ct).ConfigureAwait(false);
            var status = resp.StatusCode;
            var location = GetRedirectTarget(resp, current.RequestUri!);
            if (location is null) return resp;
            resp.Dispose();

            if (++hops > MaxRedirects)
            {
                throw new HttpRequestException(
                    $"Stopped after {MaxRedirects} redirects; the last hop pointed at '{location}'.");
            }

            await ValidateRedirectTargetAsync(origin, location, ct).ConfigureAwait(false);

            var next = CloneForRedirect(current, status, location, body, contentHeaders);
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = next;
        }
    }

    /// <summary>Absolute <c>Location</c> for a redirect status, or <c>null</c> when the response
    /// is terminal (including a 3xx with no usable <c>Location</c> — we hand that back to the
    /// caller as-is rather than inventing a target).</summary>
    private static Uri? GetRedirectTarget(HttpResponseMessage resp, Uri current)
    {
        if (resp.StatusCode is not (HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
        {
            return null;
        }
        var location = resp.Headers.Location;
        if (location is null) return null;
        if (location.IsAbsoluteUri) return location;
        return Uri.TryCreate(current, location, out var absolute) ? absolute : null;
    }

    /// <summary>
    /// Gate for one automatic hop. <paramref name="origin"/> is the URL the user actually asked
    /// for: staying on that host is always allowed — a local dev server redirecting <c>/</c> to
    /// <c>/login</c> has to keep working — and only a move to a <em>different</em> host is
    /// subject to the network policy.
    /// </summary>
    private static async Task ValidateRedirectTargetAsync(Uri origin, Uri target, CancellationToken ct)
    {
        if (target.Scheme is not ("http" or "https"))
        {
            throw new HttpRequestException(
                $"Refusing to follow a redirect to '{target.Scheme}://' — only http and https hops are followed.");
        }

        if (string.Equals(origin.DnsSafeHost, target.DnsSafeHost, StringComparison.OrdinalIgnoreCase)) return;

        IPAddress[] addresses;
        try
        {
            // Resolved rather than pattern-matched on the literal: a hostname that answers with
            // 127.0.0.1 is the standard way around a textual check.
            addresses = await Dns.GetHostAddressesAsync(target.DnsSafeHost, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // Unresolvable, so unclassifiable. The connection attempt fails on its own terms and
            // produces a better message than we could here.
            return;
        }

        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var label = ClassifyBlocked(ip);
            if (label is null) continue;
            throw new HttpRequestException(
                $"Refusing to follow a redirect to '{target}' — it resolves to {ip} ({label}). " +
                "Requesting that address directly still works; only automatic redirects into it are blocked.");
        }
    }

    private static string? ClassifyBlocked(IPAddress ip)
    {
        foreach (var (network, label) in BlockedRedirectNetworks)
            if (network.Contains(ip)) return label;

        if (BlockPrivateRedirects)
        {
            foreach (var (network, label) in PrivateRedirectNetworks)
                if (network.Contains(ip)) return label;
        }
        return null;
    }

    /// <summary>Builds the next hop. Method and body rewriting follow the rules
    /// <see cref="HttpClientHandler"/> applies (303 becomes GET unless the request was a HEAD;
    /// 300/301/302 demote a POST to GET; 307/308 replay verbatim). Headers are copied wholesale
    /// only while we stay on the same host — see <see cref="CrossHostSafeHeaders"/>.</summary>
    private static HttpRequestMessage CloneForRedirect(
        HttpRequestMessage previous,
        HttpStatusCode status,
        Uri target,
        byte[]? body,
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders)
    {
        var method = previous.Method;
        var keepBody = true;
        if ((status == HttpStatusCode.SeeOther && method != HttpMethod.Head)
            || ((status is HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found)
                && method == HttpMethod.Post))
        {
            method = HttpMethod.Get;
            keepBody = false;
        }

        var next = new HttpRequestMessage(method, target);

        if (keepBody && body is not null)
        {
            var content = new ByteArrayContent(body);
            content.Headers.Clear();
            if (contentHeaders is not null)
            {
                foreach (var (k, v) in contentHeaders) content.Headers.TryAddWithoutValidation(k, v);
            }
            next.Content = content;
        }

        var sameHost = string.Equals(
            previous.RequestUri!.DnsSafeHost, target.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
        foreach (var (name, values) in previous.Headers)
        {
            if (!sameHost && !CrossHostSafeHeaders.Contains(name)) continue;
            next.Headers.TryAddWithoutValidation(name, values);
        }
        return next;
    }
}
