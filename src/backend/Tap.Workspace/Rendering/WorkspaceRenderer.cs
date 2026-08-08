using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Two-layer resolver: file-scope cascade (workspace/collection/stage/env/request +
/// overrides) composes a flat dictionary, then <see cref="Interpolation"/> walks tokens —
/// first against the cascade, then across the <see cref="VariableProviderRegistry"/>.
///
/// <para>"Overwrite by name" — within the cascade, later scopes overwrite earlier ones; within
/// the provider list, the first match wins (registration order). The cascade as a whole takes
/// precedence over providers for unprefixed <c>{{name}}</c> tokens because per-scope file vars
/// are the most-specific source. Explicit <c>{{provider:name}}</c> bypasses the cascade.</para>
/// </summary>
public sealed class WorkspaceRenderer(LoadedWorkspace workspace, VariableProviderRegistry registry)
{
    public async ValueTask<ResolvedRequest> RenderAsync(
        RequestFile request,
        EnvFile? env,
        IReadOnlyDictionary<string, string>? overrides,
        CancellationToken ct,
        string? stageName = null)
    {
        var requestDir = Path.GetDirectoryName(request.RelativePath) ?? string.Empty;
        var collection = CollectionLocator.ForFile(workspace, request.RelativePath);
        var stage = collection?.FindStage(stageName) ?? collection?.FindStage(collection.DefaultStage);

        var auth = workspace.Resolve(request.Auth, requestDir) as AuthFile;
        if (auth is null && stage?.DefaultAuth is not null && collection is not null)
        {
            var collectionDir = Path.GetDirectoryName(collection.RelativePath) ?? string.Empty;
            auth = workspace.Resolve(stage.DefaultAuth, collectionDir) as AuthFile;
        }
        if (auth is null && collection?.DefaultAuth is not null)
        {
            var collectionDir = Path.GetDirectoryName(collection.RelativePath) ?? string.Empty;
            auth = workspace.Resolve(collection.DefaultAuth, collectionDir) as AuthFile;
        }

        // Cascade: workspace < collection < stage < env < request < overrides.
        var cascade = new Dictionary<string, string>(StringComparer.Ordinal);
        if (workspace.Manifest is not null) MergeVars(cascade, workspace.Manifest.Vars);
        if (collection is not null) MergeVars(cascade, collection.Vars);
        if (stage is not null) MergeVars(cascade, stage.Vars);
        if (env is not null) MergeVars(cascade, env.Vars);
        MergeVars(cascade, request.Vars);
        if (overrides is not null)
        {
            foreach (var (k, v) in overrides) cascade[k] = v;
        }

        var expandedBlock = await Interpolation.ExpandAsync(request.HttpBlock, cascade, registry, ct).ConfigureAwait(false);
        var parsed = HttpBlockParser.Parse(expandedBlock);

        var url = parsed.Url;
        if (!HasAnyScheme(url))
        {
            if (collection is null || string.IsNullOrWhiteSpace(collection.BaseUrl) && string.IsNullOrWhiteSpace(stage?.BaseUrl))
            {
                // The URL has already been through interpolation, so it can carry a resolved secret
                // — name the request instead of echoing it.
                throw new WorkspaceParseException(new WorkspaceError(
                    WorkspaceErrorCode.E_HTTP_BLOCK_SYNTAX,
                    "The request URL is not absolute and the owning collection has no baseUrl to fall back on.",
                    request.RelativePath));
            }
            var baseUrlSource = !string.IsNullOrWhiteSpace(stage?.BaseUrl) ? stage!.BaseUrl! : collection.BaseUrl;
            var baseUrl = await Interpolation.ExpandAsync(baseUrlSource, cascade, registry, ct).ConfigureAwait(false);
            baseUrl = NormalizeScheme(baseUrl, request.Protocol);
            url = JoinUrl(baseUrl, url);
        }
        else
        {
            url = NormalizeScheme(url, request.Protocol);
        }

        // Headers: collection.defaultHeaders < block headers < auth-derived.
        //
        // Each source is interpolated exactly once. The block's own headers and body were expanded
        // as part of `expandedBlock` above and are carried through verbatim; running them through a
        // second pass would let a resolved value that happens to contain `{{…}}` kick off another
        // round of provider lookups — second-order injection straight out of a workspace file.
        var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (collection is not null)
        {
            foreach (var (k, v) in collection.DefaultHeaders)
                resolvedHeaders[k] = await Interpolation.ExpandAsync(v, cascade, registry, ct).ConfigureAwait(false);
        }
        foreach (var (k, v) in parsed.Headers) resolvedHeaders[k] = v;

        var authHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ApplyAuthHeaders(auth, authHeaders);
        foreach (var (k, v) in authHeaders)
            resolvedHeaders[k] = await Interpolation.ExpandAsync(v, cascade, registry, ct).ConfigureAwait(false);

        // Re-check the fully assembled request line and headers: the baseUrl, the collection
        // defaults and the auth-derived values never passed through HttpBlockParser's own guard.
        HttpBlockParser.EnsureNoLineBreaks("The request URL", url);
        foreach (var (k, v) in resolvedHeaders)
        {
            HttpBlockParser.EnsureNoLineBreaks("A header name", k);
            HttpBlockParser.EnsureNoLineBreaks($"The '{k}' header value", v);
        }

        return new ResolvedRequest
        {
            Method = parsed.Method,
            Url = url,
            Headers = resolvedHeaders,
            Body = parsed.Body,
            Protocol = request.Protocol,
            Metadata = new ResolvedRequestMetadata
            {
                SourceRequestPath = request.RelativePath,
                EnvPath = env?.RelativePath,
                StageName = stage?.Name,
                VariablesUsed = registry.Trace,
            },
        };
    }

    private static void MergeVars(Dictionary<string, string> dest, IReadOnlyDictionary<string, VarSpec> src)
    {
        foreach (var (k, v) in src)
        {
            if (v.Default is { } d) dest[k] = d;
        }
    }

    private static void ApplyAuthHeaders(AuthFile? auth, IDictionary<string, string> headers)
    {
        if (auth is null || auth.Type == "none") return;

        switch (auth.Type)
        {
            case "bearer":
                if (auth.Fields.TryGetValue("token", out var token) && token is not null)
                    headers["Authorization"] = "Bearer " + token;
                break;

            case "basic":
                if (auth.Fields.TryGetValue("username", out var u) &&
                    auth.Fields.TryGetValue("password", out var p) && u is not null && p is not null)
                {
                    var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(u + ":" + p));
                    headers["Authorization"] = "Basic " + creds;
                }
                break;

            case "apiKey":
                {
                    var inLoc = auth.Fields.GetValueOrDefault("in");
                    var name = auth.Fields.GetValueOrDefault("apiKeyName") ?? auth.Fields.GetValueOrDefault("name");
                    var val = auth.Fields.GetValueOrDefault("apiKeyValue") ?? auth.Fields.GetValueOrDefault("value");
                    if (inLoc == "header" && !string.IsNullOrEmpty(name) && val is not null)
                    {
                        headers[name] = val;
                    }
                    break;
                }

            case "custom":
                foreach (var (k, v) in auth.Headers) headers[k] = v;
                break;

            case "github":
                {
                    // Static render path: only the PAT mode can produce a header without a
                    // runtime exchange. App / OAuth / gh-cli all need the AuthRunner — those
                    // requests will pick up the live token via the execute path instead.
                    var mode = (auth.Fields.GetValueOrDefault("mode") ?? "pat").Trim();
                    if (mode is "pat" or "" && auth.Fields.TryGetValue("token", out var ghToken) && !string.IsNullOrEmpty(ghToken))
                    {
                        headers["Authorization"] = "Bearer " + ghToken;
                    }
                    break;
                }
        }
    }

    private static bool HasAnyScheme(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeScheme(string url, RequestProtocol protocol)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var stripped = url.StartsWith("//") ? url[2..] : url;
        var schemeEnd = stripped.IndexOf("://", StringComparison.Ordinal);

        if (stripped != url || schemeEnd < 0)
        {
            var prefix = protocol == RequestProtocol.WebSocket ? "ws://" : "http://";
            return prefix + stripped;
        }

        var scheme = stripped[..schemeEnd].ToLowerInvariant();
        var rest = stripped[(schemeEnd + 3)..];
        return (protocol, scheme) switch
        {
            (RequestProtocol.WebSocket, "http") => "ws://" + rest,
            (RequestProtocol.WebSocket, "https") => "wss://" + rest,
            (RequestProtocol.Http, "ws") => "http://" + rest,
            (RequestProtocol.Http, "wss") => "https://" + rest,
            _ => stripped,
        };
    }

    private static string JoinUrl(string baseUrl, string path)
    {
        if (baseUrl.EndsWith('/') && path.StartsWith('/')) return baseUrl + path[1..];
        if (!baseUrl.EndsWith('/') && !path.StartsWith('/')) return baseUrl + "/" + path;
        return baseUrl + path;
    }
}
