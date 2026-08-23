using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tap.Core.Redaction;

/// <summary>
/// Strips credentials and personal data out of captured inspector traffic on its way to a
/// reader that must not see them — an agent transcript, an MCP tool result, a CLI's JSON.
///
/// <para>This is <em>not</em> <c>Tap.Workspace</c>'s <c>SecretRedactor</c>, and the difference
/// is the whole design. That one knows each secret's clear text because the renderer produced
/// it, so it can replace known values wherever they landed. The inspector receives its traffic
/// from strangers and holds no registry of what is secret, so this one only ever
/// <em>detects</em> — and detection is never complete. Three rules follow:</para>
///
/// <list type="number">
/// <item><b>Fail closed on the unknown.</b> An unrecognised content type yields metadata only —
/// kind, size, sha256 — never bytes. A detector that times out on hostile input masks the
/// whole payload rather than shipping it unscanned.</item>
/// <item><b>Preserve shape, drop value.</b> <c>Bearer [redacted:jwt #a91f3c2d len=812]</c>
/// answers the question someone is actually debugging; <c>***</c> does not.</item>
/// <item><b>Fingerprints, not values.</b> A salted short hash lets a reader say "the 401
/// carries a different token than the 200" while disclosing nothing.</item>
/// </list>
///
/// <para>There is deliberately no way to get a value back — no reveal method, no unmasked
/// mode, no accessor that returns what was hidden. The human escape hatch is the inspector UI,
/// which still holds the raw record because redaction happens at read time. That makes this
/// class auditable by absence: there is no disclosure path to review.</para>
///
/// <para>Instances are cheap but <b>not</b> interchangeable: each carries its own random salt,
/// so fingerprints correlate only within one redactor's lifetime. Hold one per inspector run.</para>
/// </summary>
public sealed partial class CaptureRedactor
{
    /// <summary>Header names that carry credentials by definition. Seeded from the same list
    /// <c>SecretRedactor</c> uses, widened for the header zoo real clients send.</summary>
    private static readonly string[] AlwaysSensitiveHeaders =
    [
        "Authorization", "Proxy-Authorization", "Authentication",
        "Cookie", "Set-Cookie",
        "X-Api-Key", "Api-Key", "X-Auth-Token", "X-Access-Token", "X-Session-Token",
        "X-Csrf-Token", "X-Xsrf-Token", "X-Amz-Security-Token",
    ];

    /// <summary>
    /// Fragments that make a header sensitive wherever they appear in its name, matched
    /// against the name with separators stripped. Real headers are named too freely for a
    /// suffix rule: GitHub sends <c>X-Hub-Signature-256</c>, where the interesting word is in
    /// the middle. Deliberately specific — a bare <c>token</c> fragment would swallow
    /// <c>X-Continuation-Token</c>, which is paging state worth reading.
    /// </summary>
    private static readonly string[] SensitiveHeaderFragments =
    [
        "signature", "apikey", "authtoken", "accesstoken", "sessiontoken",
        "secret", "password", "credential",
    ];

    private readonly CaptureRedactionOptions _options;
    private readonly HashSet<string> _sensitiveHeaders;
    private readonly SecretKeyMatcher _keys;

    /// <summary>
    /// Random per instance, held in memory, never logged and never serialized. A per-run salt
    /// is what makes fingerprints safe to hand out: they correlate within one debugging
    /// session and are worthless outside it, so nobody can build a table that turns a
    /// fingerprint back into a token.
    /// </summary>
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);

    public CaptureRedactor(CaptureRedactionOptions? options = null)
    {
        _options = options ?? CaptureRedactionOptions.Default;
        _sensitiveHeaders = new HashSet<string>(AlwaysSensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _options.ExtraSensitiveHeaders) _sensitiveHeaders.Add(name);
        _keys = new SecretKeyMatcher(_options.ExtraSecretKeys);
    }

    /// <summary>
    /// A stable, non-reversible short name for a value: <c>#a91f3c2d</c>. Equal fingerprints
    /// mean equal bytes. Returns <c>null</c> for values too short to fingerprint safely —
    /// see <see cref="CaptureRedactionOptions.MinFingerprintLength"/>.
    /// </summary>
    public string? Fingerprint(string? value)
    {
        if (value is null || value.Length < _options.MinFingerprintLength) return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_salt);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        return "#" + Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 4));
    }

    /// <summary>
    /// Scan a bare string that is not a header, URL, or body — an error message, a close
    /// reason, an upstream address. These are where a credential turns up by accident: an
    /// exception that renders the URL it failed on takes the query string with it.
    /// </summary>
    /// <param name="location">Used verbatim as the note location, since a loose string has no
    /// structure to name a position within.</param>
    public RedactedText Text(string? value, string location = "text")
    {
        var notes = new List<RedactionNote>();
        return string.IsNullOrEmpty(value)
            ? new RedactedText(value ?? string.Empty, notes)
            : new RedactedText(ScanText(value, location, notes), notes);
    }

    // ---------------------------------------------------------------- layer 1: headers

    /// <summary>
    /// Layer 1. Credential-carrying headers are masked by name; everything else is still
    /// scanned for shapes, because a token turns up in a header nobody expected often enough
    /// to matter. Names and order are preserved — both are occasionally the bug.
    /// </summary>
    public RedactedHeaders Headers(IReadOnlyDictionary<string, string> headers, string scope = "header")
    {
        var notes = new List<RedactionNote>();
        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
        {
            var location = Loc(scope, name);
            if (string.IsNullOrEmpty(value))
            {
                result[name] = value;
            }
            else if (IsCookieHeader(name))
            {
                result[name] = RedactCookieHeader(name, value, location, notes);
            }
            else if (IsSensitiveHeader(name))
            {
                result[name] = IsAuthorizationHeader(name)
                    ? RedactAuthorization(value, location, notes)
                    : MaskWithNote(value, location, RedactionReason.SensitiveHeader, notes);
            }
            else
            {
                result[name] = ScanText(value, location, notes);
            }
        }

        return new RedactedHeaders(result, notes);
    }

    private bool IsSensitiveHeader(string name)
    {
        if (_sensitiveHeaders.Contains(name)) return true;

        var flattened = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        foreach (var fragment in SensitiveHeaderFragments)
        {
            if (flattened.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsCookieHeader(string name)
        => name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizationHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the scheme, drops the credential: <c>Bearer [redacted:jwt …]</c>. Which
    /// scheme is in play is diagnostic information and never secret, and a request that sent
    /// <c>Basic</c> where you expected <c>Bearer</c> is a bug you want to see immediately.</summary>
    private string RedactAuthorization(string value, string location, List<RedactionNote> notes)
    {
        var space = value.IndexOf(' ');
        if (space <= 0) return MaskWithNote(value, location, RedactionReason.SensitiveHeader, notes);

        var scheme = value[..space];
        var credential = value[(space + 1)..].Trim();
        if (credential.Length == 0) return value;

        notes.Add(new RedactionNote(location, RedactionReason.SensitiveHeader, Fingerprint(credential)));
        var kind = CapturePatterns.LooksLikeJwt(credential) ? "jwt"
            : scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ? "basic"
            : "opaque";
        return scheme + " " + Mask(kind, credential);
    }

    /// <summary>
    /// Per-cookie, not whole-header. Masking <c>Cookie</c> outright is right for a rendered
    /// request but wrong for a captured one: <c>theme=dark</c> and <c>locale=de-CH</c> are
    /// routinely the clue you need, while the session cookie next to them is the credential.
    ///
    /// <para>The keep test leans closed — a cookie survives only if its name is not
    /// secret-shaped, its value is short, and its value does not look like a token. Anything
    /// ambiguous is masked.</para>
    ///
    /// <para>On <c>Set-Cookie</c> only the first pair is a cookie; <c>Path</c>, <c>HttpOnly</c>
    /// and friends that follow are attributes worth keeping.</para>
    /// </summary>
    private string RedactCookieHeader(string name, string value, string location, List<RedactionNote> notes)
    {
        var isSetCookie = name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);
        var parts = value.Split(';');

        for (var i = 0; i < parts.Length; i++)
        {
            if (isSetCookie && i > 0) break;

            var part = parts[i];
            var trimmed = part.TrimStart();
            var lead = part[..(part.Length - trimmed.Length)];

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var cookieName = trimmed[..eq];
            var cookieValue = trimmed[(eq + 1)..];
            if (cookieValue.Length == 0 || KeepCookie(cookieName, cookieValue)) continue;

            notes.Add(new RedactionNote(
                location + "/" + cookieName, RedactionReason.Cookie, Fingerprint(cookieValue)));
            parts[i] = lead + cookieName + "=" + MaskScalar(cookieValue);
        }

        return string.Join(';', parts);
    }

    private bool KeepCookie(string name, string value)
        => !_keys.IsSecret(name)
            && value.Length <= _options.CookieKeepMaxLength
            && !LooksLikeToken(value);

    /// <summary>High-entropy-ish: long enough, drawn from a token alphabet, and mixing letters
    /// with digits. Keeps <c>de-CH</c> and <c>dark</c>; catches <c>a1b2c3d4e5f6</c>.</summary>
    private static bool LooksLikeToken(string value)
    {
        if (value.Length < 12) return false;

        var hasDigit = false;
        var hasLetter = false;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c)) hasDigit = true;
            else if (char.IsAsciiLetter(c)) hasLetter = true;
            else if (c is not ('-' or '_' or '=' or '+' or '/' or '.' or '%')) return false;
        }

        return hasDigit && hasLetter;
    }

    // ------------------------------------------------------- layer 2: path and query

    /// <summary>
    /// Layer 2. <c>RequestRecord.Path</c> carries the query string, which is where
    /// <c>?access_token=</c> and <c>?code=</c> live — the most-forgotten leak of the lot,
    /// because nobody thinks of a URL as a body.
    /// </summary>
    public RedactedText Target(string? pathAndQuery, string scope = "query")
    {
        var notes = new List<RedactionNote>();
        if (string.IsNullOrEmpty(pathAndQuery)) return new RedactedText(pathAndQuery ?? string.Empty, notes);

        var mark = pathAndQuery.IndexOf('?');
        if (mark < 0) return new RedactedText(ScanText(pathAndQuery, Loc(scope, "path"), notes), notes);

        var path = ScanText(pathAndQuery[..mark], Loc(scope, "path"), notes);
        var query = RedactPairs(pathAndQuery[(mark + 1)..], '&', scope, queryContext: true, notes);
        return new RedactedText(path + "?" + query, notes);
    }

    // ------------------------------------------------ layers 3–5: bodies and frames

    /// <summary>
    /// Layers 3–5. Picks a strategy from the content type: structured keys for JSON and form
    /// payloads, per-part metadata for multipart, a shape scan for anything textual, and
    /// nothing at all for binary.
    ///
    /// <para>Redaction runs before any truncation the caller applies for display, so a mask is
    /// never cut in half — half a mask reads as a leak.</para>
    /// </summary>
    public RedactedBody Body(
        string? text,
        string? contentType,
        long originalSize,
        bool truncated = false,
        string scope = "body")
    {
        var notes = new List<RedactionNote>();
        if (string.IsNullOrEmpty(text))
        {
            return new RedactedBody(null, "empty", originalSize, truncated, null, notes);
        }

        var sha = Sha256Hex(text);
        var root = Loc(scope, "$");

        switch (Classify(contentType, text))
        {
            case "json":
                return new RedactedBody(RedactJson(text, scope, notes), "json", originalSize, truncated, sha, notes);
            case "form":
                return new RedactedBody(
                    RedactPairs(text, '&', scope, queryContext: false, notes),
                    "form", originalSize, truncated, sha, notes);
            case "multipart":
                return new RedactedBody(
                    SummarizeMultipart(text, contentType, scope, notes),
                    "multipart", originalSize, truncated, sha, notes);
            case "text":
                return new RedactedBody(ScanText(text, root, notes), "text", originalSize, truncated, sha, notes);
            default:
                notes.Add(new RedactionNote(root, RedactionReason.Binary, Fingerprint(text)));
                return new RedactedBody(null, "binary", originalSize, truncated, sha, notes);
        }
    }

    /// <summary>An SSE <c>data:</c> payload or a WebSocket frame. Same treatment as a body — a
    /// token refresh arriving over a WebSocket bypasses every header rule, so these cannot be
    /// the one path that skips redaction.</summary>
    public RedactedBody Frame(string? text, string? contentType = null, string scope = "frame")
        => Body(text, contentType, text is null ? 0 : Encoding.UTF8.GetByteCount(text), false, scope);

    private static string Classify(string? contentType, string text)
    {
        var mime = contentType is null ? string.Empty : contentType.Split(';')[0].Trim().ToLowerInvariant();

        if (mime.Length == 0)
        {
            // No content type at all — common for WebSocket frames. Sniff, and lean closed.
            var head = text.AsSpan().TrimStart();
            if (head.Length > 0 && (head[0] == '{' || head[0] == '[')) return "json";
            return LooksPrintable(text) ? "text" : "binary";
        }

        if (mime.Contains("json", StringComparison.Ordinal)) return "json";
        if (mime == "application/x-www-form-urlencoded") return "form";
        if (mime.StartsWith("multipart/", StringComparison.Ordinal)) return "multipart";
        if (mime.StartsWith("text/", StringComparison.Ordinal)) return "text";

        foreach (var textual in (ReadOnlySpan<string>)["xml", "html", "csv", "javascript", "graphql", "yaml", "x-ndjson"])
        {
            if (mime.Contains(textual, StringComparison.Ordinal)) return "text";
        }

        return "binary";
    }

    /// <summary>Bodies arrive as strings even when the bytes were not text, so a decoded blob
    /// shows up as control characters and U+FFFD replacements. Sample the head rather than
    /// walking a megabyte.</summary>
    private static bool LooksPrintable(string text)
    {
        var sample = text.Length <= 512 ? text.AsSpan() : text.AsSpan(0, 512);
        var printable = 0;
        foreach (var c in sample)
        {
            if (c == '�') continue;
            if (!char.IsControl(c) || c is '\r' or '\n' or '\t') printable++;
        }

        return printable * 100 >= sample.Length * 95;
    }

    // -------------------------------------------------------------------- layer 3: JSON

    private string RedactJson(string text, string scope, List<RedactionNote> notes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            // Very common rather than exotic: the store caps bodies at 1 MB, so a large
            // payload arrives here cut mid-object. Falling back to a shape scan alone would
            // drop key-based masking exactly when it matters, so run a lexical pass first —
            // it finds "password": "…" in text that will never parse.
            notes.Add(new RedactionNote(Loc(scope, "$"), RedactionReason.Unparseable, null));
            var lexical = RedactJsonKeysLexically(text, scope, notes);
            return ScanText(lexical, Loc(scope, "$"), notes);
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(
                       buffer,
                       new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteRedacted(writer, document.RootElement, "$", scope, notes);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    private void WriteRedacted(
        Utf8JsonWriter writer, JsonElement element, string path, string scope, List<RedactionNote> notes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path + "." + property.Name;

                    // Only scalars are masked by key name. A secret-shaped key holding an
                    // object — `"auth": { "user": …, "password": … }` — is structure worth
                    // keeping; recursing finds the actual secret one level down instead of
                    // blanking the shape that explains the request.
                    if (_keys.IsSecret(property.Name) && IsMaskableScalar(property.Value))
                    {
                        var raw = ScalarText(property.Value);
                        notes.Add(new RedactionNote(
                            Loc(scope, childPath), RedactionReason.KnownKey, Fingerprint(raw)));
                        writer.WriteString(property.Name, MaskScalar(raw));
                    }
                    else
                    {
                        writer.WritePropertyName(property.Name);
                        WriteRedacted(writer, property.Value, childPath, scope, notes);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item, $"{path}[{index++}]", scope, notes);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(ScanText(element.GetString() ?? string.Empty, Loc(scope, path), notes));
                break;

            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }

    /// <summary><c>null</c> is left alone deliberately: <c>"token": null</c> is a fact about
    /// the request, and masking it would invent a secret that was never sent.</summary>
    private static bool IsMaskableScalar(JsonElement element)
        => element.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False;

    private static string ScalarText(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();

    private string RedactJsonKeysLexically(string text, string scope, List<RedactionNote> notes)
    {
        try
        {
            return JsonStringProperty().Replace(text, match =>
            {
                var key = match.Groups[1].Value;
                if (!_keys.IsSecret(key)) return match.Value;

                var raw = match.Groups[2].Value;
                notes.Add(new RedactionNote(
                    Loc(scope, "$." + key), RedactionReason.KnownKey, Fingerprint(raw)));
                return $"\"{key}\":\"{MaskScalar(raw)}\"";
            });
        }
        catch (RegexMatchTimeoutException)
        {
            notes.Add(new RedactionNote(Loc(scope, "$"), RedactionReason.ScanTimeout, Fingerprint(text)));
            return Mask("payload", text);
        }
    }

    [GeneratedRegex("\"([A-Za-z0-9_.\\-]{1,64})\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.None, 1000)]
    private static partial Regex JsonStringProperty();

    // ------------------------------------------------------ layer 3: query and form pairs

    private string RedactPairs(
        string text, char separator, string scope, bool queryContext, List<RedactionNote> notes)
    {
        var parts = text.Split(separator);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                parts[i] = ScanText(part, Loc(scope, part), notes);
                continue;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (value.Length == 0) continue;

            var name = Unescape(key);
            var secret = queryContext ? _keys.IsSecretQueryKey(name) : _keys.IsSecret(name);
            if (secret)
            {
                var raw = Unescape(value);
                notes.Add(new RedactionNote(Loc(scope, name), RedactionReason.KnownKey, Fingerprint(raw)));
                parts[i] = key + "=" + MaskScalar(raw);
            }
            else
            {
                parts[i] = key + "=" + ScanText(value, Loc(scope, name), notes);
            }
        }

        return string.Join(separator, parts);
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    // ------------------------------------------------------------- layer 5: multipart

    /// <summary>
    /// Metadata only, never bytes. A multipart upload is where files live, and a file is the
    /// one payload whose contents we can say nothing useful about but could leak everything
    /// through — an ID photo, a bank statement, a database dump.
    /// </summary>
    private string SummarizeMultipart(string text, string? contentType, string scope, List<RedactionNote> notes)
    {
        var boundary = ExtractBoundary(contentType);
        if (boundary is null)
        {
            notes.Add(new RedactionNote(Loc(scope, "$"), RedactionReason.Binary, Fingerprint(text)));
            return "[multipart: boundary unknown, content withheld]";
        }

        var summary = new StringBuilder();
        var lines = new List<string>();
        var index = 0;

        foreach (var segment in text.Split("--" + boundary, StringSplitOptions.None))
        {
            var part = segment.TrimStart('\r', '\n');
            if (part.Length == 0 || part.StartsWith("--", StringComparison.Ordinal)) continue;

            var split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var gap = 4;
            if (split < 0)
            {
                split = part.IndexOf("\n\n", StringComparison.Ordinal);
                gap = 2;
            }

            if (split < 0) continue;

            var headers = part[..split];
            var content = part[(split + gap)..].TrimEnd('\r', '\n');

            index++;
            var line = new StringBuilder($"part {index}:");
            Append(line, "name", HeaderParameter(headers, "name"));
            Append(line, "filename", HeaderParameter(headers, "filename"));
            Append(line, "contentType", PartContentType(headers));
            line.Append(" size=").Append(Encoding.UTF8.GetByteCount(content));
            line.Append(" sha256=").Append(Sha256Hex(content)[..16]);
            lines.Add(line.ToString());

            notes.Add(new RedactionNote(
                Loc(scope, $"$.part[{index - 1}]"), RedactionReason.Binary, Fingerprint(content)));
        }

        summary.Append("[multipart: ").Append(lines.Count).Append(lines.Count == 1 ? " part]" : " parts]");
        foreach (var line in lines) summary.Append('\n').Append(line);
        return summary.ToString();

        static void Append(StringBuilder target, string label, string? value)
        {
            if (value is not null) target.Append(' ').Append(label).Append('=').Append(value);
        }
    }

    private static string? ExtractBoundary(string? contentType)
    {
        if (contentType is null) return null;
        var at = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var value = contentType[(at + "boundary=".Length)..].Trim();
        var end = value.IndexOf(';');
        if (end >= 0) value = value[..end];
        value = value.Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    private static string? HeaderParameter(string headers, string parameter)
    {
        var at = headers.IndexOf(parameter + "=\"", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var start = at + parameter.Length + 2;
        var end = headers.IndexOf('"', start);
        return end < 0 ? null : headers[start..end];
    }

    private static string? PartContentType(string headers)
    {
        foreach (var line in headers.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Content-Type:".Length..].Trim();
            }
        }

        return null;
    }

    // -------------------------------------------------------------- layer 4: shape scan

    /// <summary>
    /// Layer 4, and the last line of defence everywhere else: replace anything that
    /// <em>looks</em> like a credential or personal datum, wherever it turned up.
    ///
    /// <para>A detector timing out means the input was built to make it time out, so the
    /// payload is masked whole rather than returned unscanned — the one outcome that cannot
    /// leak.</para>
    /// </summary>
    private string ScanText(string text, string location, List<RedactionNote> notes)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            foreach (var detector in CapturePatterns.All)
            {
                text = detector.Pattern.Replace(text, match =>
                {
                    if (detector.Accept is not null && !detector.Accept(match.Value)) return match.Value;

                    notes.Add(new RedactionNote(
                        location, RedactionReason.Pattern(detector.Kind), Fingerprint(match.Value)));
                    return Mask(detector.Kind, match.Value);
                });
            }

            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            notes.Add(new RedactionNote(location, RedactionReason.ScanTimeout, Fingerprint(text)));
            return Mask("payload", text);
        }
    }

    // ------------------------------------------------------------------------- masking

    /// <summary>Greppable and machine-parseable: <c>[redacted:jwt #a91f3c2d len=812]</c>. The
    /// length is kept because "the token is 132 characters" is often the difference between a
    /// truncated credential and a wrong one.</summary>
    private string Mask(string kind, string value)
    {
        var fingerprint = Fingerprint(value);
        var head = fingerprint is null
            ? $"[redacted:{kind} len={value.Length}"
            : $"[redacted:{kind} {fingerprint} len={value.Length}";

        // A JWT gets described rather than merely hidden. Its signature — the part that makes
        // it usable — is dropped either way; what survives is the header and the registered
        // claims, which answer the questions people actually have about a token they cannot
        // see: is it expired, does it carry the scope, is it from the right issuer.
        if (kind == "jwt")
        {
            var preview = JwtPreview.Describe(value, _options.JwtClaims, Fingerprint);
            if (preview is not null) head += " " + preview;
        }

        return head + "]";
    }

    private string MaskScalar(string raw)
        => Mask(CapturePatterns.LooksLikeJwt(raw) ? "jwt" : "opaque", raw);

    private string MaskWithNote(string value, string location, string reason, List<RedactionNote> notes)
    {
        notes.Add(new RedactionNote(location, reason, Fingerprint(value)));
        return MaskScalar(value);
    }

    private static string Loc(string scope, string detail) => scope + ":" + detail;

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
