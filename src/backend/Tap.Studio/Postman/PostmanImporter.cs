using System.Text;
using System.Text.Json;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;

namespace Tap.Studio.Postman;

/// <summary>
/// Imports a Postman v2.1 collection JSON into the Tap workspace shape:
/// a single <c>_collection.md</c>, a folder tree mirroring Postman's nested <c>item[]</c>
/// arrays, one <c>.req.md</c> per terminal item, and (optionally) one
/// <c>.auth.md</c> at workspace root when the Postman collection carries auth.
///
/// <para>The importer is a pure planner: <see cref="Plan"/> walks the JSON tree and
/// returns an <see cref="ImportPlan"/> of (relative path, content) pairs. The caller
/// (the endpoint) writes them through <see cref="WorkspaceService.Save"/> so the same
/// validation + path-safety rules apply as any other Studio write.</para>
///
/// <para>Postman variants the importer normalizes:</para>
/// <list type="bullet">
///   <item><c>url</c> may be a string or an object with <c>raw</c> / <c>host</c> /
///         <c>path</c> / <c>query</c>. The importer prefers <c>raw</c> and falls back
///         to the structured form.</item>
///   <item><c>body.mode</c> = raw|urlencoded|formdata|graphql. Raw is copied verbatim;
///         the others are flattened into the closest HTTP equivalent and the relevant
///         <c>Content-Type</c> header is set when missing.</item>
///   <item>Headers with <c>disabled: true</c> are dropped. Postman's <c>{{var}}</c>
///         template syntax matches Tap's, so values are kept verbatim.</item>
///   <item>Collection-level <c>auth</c> becomes a single auth file referenced by
///         <c>defaultAuth</c>. Per-item auth (rare in real collections) currently
///         lands in the request body markdown as a TODO marker so the user sees it
///         and can wire it up by hand.</item>
/// </list>
/// </summary>
public static class PostmanImporter
{
    private const string CollectionsRoot = "collections";
    private const string AuthRoot = "auth";

    public sealed record ImportFile(string RelativePath, string Content);

    public sealed record ImportPlan(
        string Slug,
        string CollectionPath,
        string? AuthPath,
        IReadOnlyList<ImportFile> Files,
        IReadOnlyList<string> Warnings)
    {
        public int RequestCount => Files.Count(f => f.RelativePath.EndsWith(".req.md", StringComparison.OrdinalIgnoreCase));
        public int FolderCount { get; init; }
    }

    /// <summary>Plans the import. Pass <paramref name="requestedSlug"/> to override the
    /// slug derived from <c>info.name</c>. Throws <see cref="PostmanImportException"/>
    /// when the input isn't a recognizable v2.1 collection.</summary>
    public static ImportPlan Plan(JsonElement root, string? requestedSlug)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PostmanImportException("not-a-collection", "Top-level JSON is not an object.");
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new PostmanImportException("not-a-collection", "Missing 'info' object — not a Postman collection.");
        if (!root.TryGetProperty("item", out var topItems) || topItems.ValueKind != JsonValueKind.Array)
            throw new PostmanImportException("not-a-collection", "Missing 'item' array — not a Postman collection.");

        var schema = info.TryGetProperty("schema", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
        var warnings = new List<string>();
        if (schema is { Length: > 0 } && !schema.Contains("v2.1", StringComparison.OrdinalIgnoreCase) && !schema.Contains("v2.0", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Schema '{schema}' is not v2.1 — best-effort import.");

        var collectionName = info.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
            ? nm.GetString() ?? "Collection"
            : "Collection";
        var description = ExtractDescription(info);

        var slug = string.IsNullOrWhiteSpace(requestedSlug)
            ? Slugify(collectionName)
            : Slugify(requestedSlug);
        if (string.IsNullOrEmpty(slug))
            throw new PostmanImportException("invalid-slug", "Could not derive a slug from the collection name.");

        var (baseUrl, vars) = ExtractVariables(root);

        // Collection-level auth → write to auth/<slug>-postman.auth.md and reference from defaultAuth.
        string? defaultAuth = null;
        string? authPath = null;
        ImportFile? authFile = null;
        if (root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            var authSpec = TryMapAuth(authEl, $"{collectionName} (Postman)", warnings);
            if (authSpec is not null)
            {
                var authSlug = $"{slug}-postman";
                authPath = $"{AuthRoot}/{authSlug}.auth.md";
                authFile = new ImportFile(authPath, AuthSpecEmitter.ToFileSource(authSpec with { Path = authPath }));
                // Auth file lives at auth/X; request files live at collections/<slug>/<sub>/Y.
                // For the collection's defaultAuth we encode the relative path from the
                // collection file to the auth file.
                defaultAuth = $"../../{authPath}";
            }
        }

        var collectionSpec = new CollectionSpecDto
        {
            Slug = slug,
            Id = null,
            Name = collectionName,
            BaseUrl = baseUrl,
            DefaultAuth = defaultAuth,
            Vars = vars.Count > 0 ? vars : null,
            Body = string.IsNullOrWhiteSpace(description) ? null : description,
        };
        var collectionRelPath = $"{CollectionsRoot}/{slug}/_collection.md";
        var collectionContent = CollectionSpecEmitter.ToFileSource(collectionSpec);

        var files = new List<ImportFile> { new(collectionRelPath, collectionContent) };
        if (authFile is not null) files.Add(authFile);

        var folderCount = 0;
        WalkItems(
            topItems,
            parentPath: $"{CollectionsRoot}/{slug}",
            depth: 0,
            usedNames: new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
            files: files,
            warnings: warnings,
            ref folderCount);

        return new ImportPlan(slug, collectionRelPath, authPath, files, warnings) { FolderCount = folderCount };
    }

    private static void WalkItems(
        JsonElement items,
        string parentPath,
        int depth,
        Dictionary<string, HashSet<string>> usedNames,
        List<ImportFile> files,
        List<string> warnings,
        ref int folderCount)
    {
        if (!usedNames.TryGetValue(parentPath, out var siblings))
        {
            siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            usedNames[parentPath] = siblings;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "untitled"
                : "untitled";

            // Folder: nested item array, no request.
            if (item.TryGetProperty("item", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                var folderSlug = UniqueSlug(name, siblings);
                folderCount++;
                WalkItems(nested, $"{parentPath}/{folderSlug}", depth + 1, usedNames, files, warnings, ref folderCount);
                continue;
            }

            // Request: terminal item with a 'request' object.
            if (!item.TryGetProperty("request", out var req)) continue;

            var requestSpec = TryMapRequest(item, req, name, warnings);
            if (requestSpec is null) continue;

            var reqSlug = UniqueSlug(name, siblings);
            var relPath = $"{parentPath}/{reqSlug}.req.md";
            files.Add(new ImportFile(relPath, RequestSpecEmitter.ToFileSource(requestSpec with { Path = relPath })));
        }
    }

    private static RequestSpecDto? TryMapRequest(JsonElement item, JsonElement req, string name, List<string> warnings)
    {
        string method = "GET";
        if (req.ValueKind == JsonValueKind.Object && req.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
            method = (m.GetString() ?? "GET").Trim().ToUpperInvariant();
        else if (req.ValueKind == JsonValueKind.String)
        {
            // Postman supports a shorthand where `request` is just a URL string. Method defaults to GET.
            return new RequestSpecDto
            {
                Path = "(placeholder)",
                Name = name,
                Method = "GET",
                Url = req.GetString() ?? "/",
            };
        }

        var url = ExtractUrl(req, warnings);
        var headers = ExtractHeaders(req);

        string? body = null;
        if (req.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
        {
            body = MapBody(bodyEl, headers, warnings);
        }

        // Per-item auth — flag as TODO. The runner's request-level auth ref is a path
        // to an .auth.md file; building one per request would explode the workspace,
        // so we surface a warning instead and let the user wire it manually.
        if (req.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            warnings.Add($"Request '{name}' has per-request auth ({GetAuthType(auth) ?? "unknown"}). Wire it manually after import.");
        }

        // Description from the item lands in the request markdown body.
        string? markdown = ExtractDescription(item);

        return new RequestSpecDto
        {
            Path = "(placeholder)",
            Name = name,
            Method = method,
            Url = url,
            Headers = headers.Select(h => new HttpHeaderSpecDto(h.Key, h.Value)).ToArray(),
            RequestBody = body,
            Body = markdown,
        };
    }

    private static string ExtractUrl(JsonElement req, List<string> warnings)
    {
        if (!req.TryGetProperty("url", out var url)) return "/";
        if (url.ValueKind == JsonValueKind.String) return url.GetString() ?? "/";
        if (url.ValueKind != JsonValueKind.Object) return "/";

        // Prefer the raw string when present — it's what the user typed.
        if (url.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
        {
            var v = raw.GetString();
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        }

        var sb = new StringBuilder();
        if (url.TryGetProperty("protocol", out var proto) && proto.ValueKind == JsonValueKind.String)
        {
            sb.Append(proto.GetString()).Append("://");
        }
        if (url.TryGetProperty("host", out var host))
        {
            if (host.ValueKind == JsonValueKind.Array)
                sb.Append(string.Join(".", host.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())));
            else if (host.ValueKind == JsonValueKind.String)
                sb.Append(host.GetString());
        }
        if (url.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.String)
            sb.Append(':').Append(port.GetString());
        if (url.TryGetProperty("path", out var path))
        {
            if (path.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in path.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.String) continue;
                    sb.Append('/').Append(p.GetString());
                }
            }
            else if (path.ValueKind == JsonValueKind.String)
            {
                var v = path.GetString();
                if (!string.IsNullOrEmpty(v))
                {
                    if (!v.StartsWith('/')) sb.Append('/');
                    sb.Append(v);
                }
            }
        }
        if (url.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Array)
        {
            var first = true;
            foreach (var q in query.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object) continue;
                if (q.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True) continue;
                var key = q.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                var value = q.TryGetProperty("value", out var v) ? v.GetString() : null;
                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(key).Append('=').Append(value ?? string.Empty);
            }
        }
        var result = sb.ToString();
        return result.Length == 0 ? "/" : result;
    }

    private static List<KeyValuePair<string, string>> ExtractHeaders(JsonElement req)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!req.TryGetProperty("header", out var hdr) || hdr.ValueKind != JsonValueKind.Array) return list;
        foreach (var h in hdr.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.Object) continue;
            if (h.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True) continue;
            var key = h.TryGetProperty("key", out var k) ? k.GetString() : null;
            if (string.IsNullOrEmpty(key)) continue;
            var value = h.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;
            list.Add(new KeyValuePair<string, string>(key, value));
        }
        return list;
    }

    private static string? MapBody(JsonElement bodyEl, List<KeyValuePair<string, string>> headers, List<string> warnings)
    {
        var mode = bodyEl.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        switch (mode)
        {
            case "raw":
            {
                var raw = bodyEl.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                // Postman tags raw bodies with a language hint in options.raw.language; surface
                // a content-type header when none is set.
                if (bodyEl.TryGetProperty("options", out var opts)
                    && opts.ValueKind == JsonValueKind.Object
                    && opts.TryGetProperty("raw", out var rawOpts)
                    && rawOpts.ValueKind == JsonValueKind.Object
                    && rawOpts.TryGetProperty("language", out var lang)
                    && lang.ValueKind == JsonValueKind.String)
                {
                    var ct = lang.GetString() switch
                    {
                        "json" => "application/json",
                        "xml" => "application/xml",
                        "html" => "text/html",
                        "javascript" => "application/javascript",
                        "text" => "text/plain",
                        _ => null,
                    };
                    if (ct is not null) EnsureHeader(headers, "Content-Type", ct);
                }
                return raw;
            }
            case "urlencoded":
            {
                EnsureHeader(headers, "Content-Type", "application/x-www-form-urlencoded");
                if (!bodyEl.TryGetProperty("urlencoded", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
                var sb = new StringBuilder();
                var first = true;
                foreach (var kv in arr.EnumerateArray())
                {
                    if (kv.ValueKind != JsonValueKind.Object) continue;
                    if (kv.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True) continue;
                    var key = kv.TryGetProperty("key", out var k) ? k.GetString() : null;
                    if (string.IsNullOrEmpty(key)) continue;
                    var value = kv.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;
                    if (!first) sb.Append('&');
                    first = false;
                    sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
                }
                return sb.ToString();
            }
            case "graphql":
            {
                EnsureHeader(headers, "Content-Type", "application/json");
                if (!bodyEl.TryGetProperty("graphql", out var gql) || gql.ValueKind != JsonValueKind.Object) return null;
                var query = gql.TryGetProperty("query", out var q) ? q.GetString() : null;
                var vars = gql.TryGetProperty("variables", out var vr) ? vr.GetString() : null;
                var payload = new
                {
                    query = query ?? string.Empty,
                    variables = vars,
                };
                // Postman stores variables as a JSON-encoded string; embed it verbatim if
                // it parses, otherwise null it out. Use a small writer instead of JsonSerializer
                // so we don't have to register a context for an anonymous type.
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
                {
                    w.WriteStartObject();
                    w.WriteString("query", query ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(vars))
                    {
                        try
                        {
                            using var parsed = JsonDocument.Parse(vars);
                            w.WritePropertyName("variables");
                            parsed.RootElement.WriteTo(w);
                        }
                        catch (JsonException)
                        {
                            w.WriteNull("variables");
                        }
                    }
                    else w.WriteNull("variables");
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            case "formdata":
            {
                warnings.Add("formdata body imported as commented placeholder — Studio doesn't have a multipart editor yet.");
                var sb = new StringBuilder();
                sb.Append("# formdata body — review and convert manually\n");
                if (bodyEl.TryGetProperty("formdata", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var kv in arr.EnumerateArray())
                    {
                        if (kv.ValueKind != JsonValueKind.Object) continue;
                        var key = kv.TryGetProperty("key", out var k) ? k.GetString() : null;
                        var value = kv.TryGetProperty("value", out var v) ? v.GetString() : null;
                        var type = kv.TryGetProperty("type", out var t) ? t.GetString() : null;
                        sb.Append("# ").Append(key).Append(" = ").Append(value).Append(type == "file" ? "  (file)" : string.Empty).Append('\n');
                    }
                }
                return sb.ToString();
            }
            case "file":
                warnings.Add("file body imported as comment — upload the file via the Studio editor and reference it with a `< ./.files/...` ref.");
                return "# file body — upload via Studio and replace with a `< ./.files/foo.bin` ref\n";
            default:
                return null;
        }
    }

    private static (string? baseUrl, Dictionary<string, string> vars) ExtractVariables(JsonElement root)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        string? baseUrl = null;
        if (!root.TryGetProperty("variable", out var vs) || vs.ValueKind != JsonValueKind.Array)
            return (baseUrl, vars);
        foreach (var v in vs.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            var key = v.TryGetProperty("key", out var k) ? k.GetString() : null;
            if (string.IsNullOrEmpty(key)) continue;
            var value = v.TryGetProperty("value", out var vv) ? (vv.ValueKind == JsonValueKind.String ? vv.GetString() : vv.ToString()) : null;
            if (value is null) continue;
            // Common idiom: a `baseUrl` (or any case variant) variable gets promoted to
            // the collection's baseUrl so the editor's URL field reads as a relative path.
            if (baseUrl is null && (key.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)
                                  || key.Equals("base_url", StringComparison.OrdinalIgnoreCase)
                                  || key.Equals("host", StringComparison.OrdinalIgnoreCase)))
            {
                baseUrl = value;
                continue;
            }
            vars[key] = value;
        }
        return (baseUrl, vars);
    }

    private static AuthSpecDto? TryMapAuth(JsonElement auth, string name, List<string> warnings)
    {
        var type = GetAuthType(auth);
        if (type is null) return null;

        switch (type)
        {
            case "bearer":
            {
                var token = ReadAuthField(auth, "bearer", "token");
                return new AuthSpecDto { Path = "(placeholder)", Name = name, Type = "bearer", Token = token };
            }
            case "basic":
            {
                var user = ReadAuthField(auth, "basic", "username");
                var pwd = ReadAuthField(auth, "basic", "password");
                return new AuthSpecDto { Path = "(placeholder)", Name = name, Type = "basic", Username = user, Password = pwd };
            }
            case "apikey":
            {
                var key = ReadAuthField(auth, "apikey", "key");
                var value = ReadAuthField(auth, "apikey", "value");
                var inField = ReadAuthField(auth, "apikey", "in") ?? "header";
                return new AuthSpecDto
                {
                    Path = "(placeholder)",
                    Name = name,
                    Type = "apiKey",
                    In = inField,
                    ApiKeyName = key,
                    ApiKeyValue = value,
                };
            }
            case "oauth2":
            {
                var clientId = ReadAuthField(auth, "oauth2", "clientId");
                var clientSecret = ReadAuthField(auth, "oauth2", "clientSecret");
                var tokenUrl = ReadAuthField(auth, "oauth2", "accessTokenUrl") ?? ReadAuthField(auth, "oauth2", "tokenUrl");
                var authorizeUrl = ReadAuthField(auth, "oauth2", "authUrl") ?? ReadAuthField(auth, "oauth2", "authorizeUrl");
                var scopeStr = ReadAuthField(auth, "oauth2", "scope");
                var grantType = ReadAuthField(auth, "oauth2", "grant_type") ?? ReadAuthField(auth, "oauth2", "grantType");
                var flow = grantType switch
                {
                    "client_credentials" => "client_credentials",
                    "password_credentials" or "password" => "password",
                    _ => "authorization_code_pkce",
                };
                var scopes = string.IsNullOrEmpty(scopeStr)
                    ? null
                    : scopeStr!.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new AuthSpecDto
                {
                    Path = "(placeholder)",
                    Name = name,
                    Type = "oauth2",
                    Flow = flow,
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    TokenUrl = tokenUrl,
                    AuthorizeUrl = authorizeUrl,
                    Scopes = scopes,
                };
            }
            case "noauth":
                return null;
            default:
                warnings.Add($"Collection-level auth type '{type}' is not supported — auth not imported.");
                return null;
        }
    }

    private static string? GetAuthType(JsonElement auth)
    {
        if (!auth.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
        return t.GetString();
    }

    /// <summary>Postman 2.1 stores auth fields as either an array of {key,value,type}
    /// or a flat object. Handle both shapes.</summary>
    private static string? ReadAuthField(JsonElement auth, string section, string field)
    {
        if (!auth.TryGetProperty(section, out var s)) return null;
        if (s.ValueKind == JsonValueKind.Array)
        {
            foreach (var kv in s.EnumerateArray())
            {
                if (kv.ValueKind != JsonValueKind.Object) continue;
                var k = kv.TryGetProperty("key", out var kk) ? kk.GetString() : null;
                if (!string.Equals(k, field, StringComparison.OrdinalIgnoreCase)) continue;
                var v = kv.TryGetProperty("value", out var vv) ? vv.GetString() : null;
                return v;
            }
            return null;
        }
        if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty(field, out var direct))
        {
            return direct.ValueKind == JsonValueKind.String ? direct.GetString() : direct.ToString();
        }
        return null;
    }

    private static string? ExtractDescription(JsonElement el)
    {
        if (!el.TryGetProperty("description", out var d)) return null;
        if (d.ValueKind == JsonValueKind.String) return d.GetString();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString();
        return null;
    }

    private static void EnsureHeader(List<KeyValuePair<string, string>> headers, string name, string value)
    {
        foreach (var h in headers)
        {
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)) return;
        }
        headers.Add(new KeyValuePair<string, string>(name, value));
    }

    private static string UniqueSlug(string name, HashSet<string> siblings)
    {
        var baseSlug = Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "item";
        var slug = baseSlug;
        var i = 2;
        while (!siblings.Add(slug))
        {
            slug = $"{baseSlug}-{i++}";
        }
        return slug;
    }

    private static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        bool lastDash = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (ch is '_' or '-')
            {
                if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\' or '.' or ':')
            {
                if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 60 ? s[..60].TrimEnd('-') : s;
    }
}

public sealed class PostmanImportException : Exception
{
    public string Code { get; }
    public PostmanImportException(string code, string message) : base(message) { Code = code; }
}
