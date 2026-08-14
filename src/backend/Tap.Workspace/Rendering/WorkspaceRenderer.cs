using Tap.Workspace.Asserts;
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
    /// <param name="templateOverrides">Overrides whose <em>values</em> are templates, expanded
    /// against everything below them before they join the cascade. A flow step's <c>vars:</c>
    /// arrive here: <c>id: '{{orderId}}'</c> has to see the value an earlier step bound, and a
    /// plain override would land in the cascade unexpanded and go out on the wire verbatim.
    /// Applied after <paramref name="overrides"/>, in order, each seeing the ones before it.</param>
    public async ValueTask<ResolvedRequest> RenderAsync(
        RequestFile request,
        EnvFile? env,
        IReadOnlyDictionary<string, string>? overrides,
        CancellationToken ct,
        string? stageName = null,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides = null)
    {
        var requestDir = Path.GetDirectoryName(request.RelativePath) ?? string.Empty;
        var collection = CollectionLocator.ForFile(workspace, request.RelativePath);
        var stage = collection?.FindStage(stageName) ?? collection?.FindStage(collection.DefaultStage);
        var transport = new RequestTransportSettings
        {
            IgnoreTlsErrors = request.Transport.IgnoreTlsErrors ?? collection?.Transport.IgnoreTlsErrors,
            TimeoutMs = request.Transport.TimeoutMs ?? collection?.Transport.TimeoutMs,
        };

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

        var (cascade, secretNames) = BuildCascade(request, collection, stage, env, overrides);
        await ApplyTemplateOverridesAsync(cascade, secretNames, templateOverrides, ct).ConfigureAwait(false);

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

        var assertions = await RenderAssertionsAsync(request.Assertions, cascade, secretNames, ct).ConfigureAwait(false);

        return new ResolvedRequest
        {
            Method = parsed.Method,
            Url = url,
            Headers = resolvedHeaders,
            Body = parsed.Body,
            Protocol = request.Protocol,
            Transport = transport,
            Assertions = assertions,
            Metadata = new ResolvedRequestMetadata
            {
                SourceRequestPath = request.RelativePath,
                EnvPath = env?.RelativePath,
                StageName = stage?.Name,
                VariablesUsed = registry.Trace,
            },
        };
    }

    /// <summary>
    /// Renders only a request's assertions. Used by the Studio's re-check path, which
    /// evaluates edited assertions against the response already on screen — same cascade,
    /// same providers, same secret tracking as a real execution, minus the request itself.
    /// </summary>
    /// <param name="tolerant">When true, an assertion that fails to expand (an unknown
    /// variable, most often a half-typed token) is returned carrying a
    /// <see cref="ResolvedAssert.RenderError"/> instead of taking the whole batch down with
    /// it. The interactive path wants this; an execution does not.</param>
    public async ValueTask<IReadOnlyList<ResolvedAssert>> RenderAssertionsAsync(
        RequestFile request,
        EnvFile? env,
        IReadOnlyList<AssertSpec> assertions,
        CancellationToken ct,
        string? stageName = null,
        bool tolerant = false,
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides = null)
    {
        var collection = CollectionLocator.ForFile(workspace, request.RelativePath);
        var stage = collection?.FindStage(stageName) ?? collection?.FindStage(collection.DefaultStage);
        var (cascade, secretNames) = BuildCascade(request, collection, stage, env, overrides);
        await ApplyTemplateOverridesAsync(cascade, secretNames, templateOverrides, ct).ConfigureAwait(false);
        return await RenderAssertionsAsync(assertions, cascade, secretNames, ct, tolerant).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds template-valued overrides into a built cascade. Each is expanded against the
    /// cascade as it stands, so a later one can read an earlier one, and a value that pulled in
    /// something secret is marked so an assertion built from it reports <c>***</c>.
    /// </summary>
    private async ValueTask ApplyTemplateOverridesAsync(
        Dictionary<string, string> cascade,
        HashSet<string> secretNames,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides,
        CancellationToken ct)
    {
        if (templateOverrides is not { Count: > 0 }) return;

        foreach (var (name, template) in templateOverrides)
        {
            var (value, secret) = await ExpandTrackingSecretsAsync(template, cascade, secretNames, ct)
                .ConfigureAwait(false);
            cascade[name] = value;
            if (secret) secretNames.Add(name);
            else secretNames.Remove(name);
        }
    }

    /// <summary>Composes the variable cascade — workspace &lt; collection &lt; stage &lt; env
    /// &lt; request &lt; overrides — along with the set of names whose winning definition is
    /// marked secret.</summary>
    private (Dictionary<string, string> Cascade, HashSet<string> SecretNames) BuildCascade(
        RequestFile request,
        CollectionFile? collection,
        CollectionStage? stage,
        EnvFile? env,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var cascade = new Dictionary<string, string>(StringComparer.Ordinal);
        // Sensitivity is tracked alongside the values because merging keeps only the value —
        // and an assertion needs to know whether the expected value it just expanded is safe
        // to echo back into a results pane.
        var secretNames = new HashSet<string>(StringComparer.Ordinal);

        if (workspace.Manifest is not null) MergeVars(cascade, workspace.Manifest.Vars, secretNames);
        if (collection is not null) MergeVars(cascade, collection.Vars, secretNames);
        if (stage is not null) MergeVars(cascade, stage.Vars, secretNames);
        if (env is not null) MergeVars(cascade, env.Vars, secretNames);
        MergeVars(cascade, request.Vars, secretNames);

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                cascade[k] = v;
                // A per-run override is a literal the caller just typed, not a secret lookup.
                secretNames.Remove(k);
            }
        }

        return (cascade, secretNames);
    }

    private static void MergeVars(
        Dictionary<string, string> dest, IReadOnlyDictionary<string, VarSpec> src, HashSet<string> secretNames)
    {
        foreach (var (k, v) in src)
        {
            if (v.Default is not { } d) continue;
            dest[k] = d;
            // A later scope redefining the name also redefines its sensitivity — an env that
            // overrides a secret with a literal test value is no longer holding a secret.
            if (v.Secret) secretNames.Add(k);
            else secretNames.Remove(k);
        }
    }

    /// <summary>
    /// Expands the selectors and expected values of a request's assertions. Runs against the
    /// same cascade and registry as the request body, so <c>equals: "{{user.email}}"</c>
    /// compares against the very value the request was built with.
    ///
    /// <para>Sensitivity is tracked on the expected side only, and only to decide whether the
    /// reported expectation is masked. Selectors — a header name, a path expression — stay
    /// visible; they are structural, and no more revealing than the fully-expanded URL and
    /// headers the Request tab already shows.</para>
    /// </summary>
    private async ValueTask<IReadOnlyList<ResolvedAssert>> RenderAssertionsAsync(
        IReadOnlyList<AssertSpec> assertions,
        IReadOnlyDictionary<string, string> cascade,
        IReadOnlySet<string> secretNames,
        CancellationToken ct,
        bool tolerant = false)
    {
        if (assertions.Count == 0) return [];

        var resolved = new List<ResolvedAssert>(assertions.Count);
        foreach (var assertion in assertions)
        {
            if (tolerant)
            {
                try
                {
                    resolved.Add(await RenderOneAsync(assertion, cascade, secretNames, ct).ConfigureAwait(false));
                }
                catch (WorkspaceParseException ex)
                {
                    resolved.Add(new ResolvedAssert(assertion, false, ex.Error.Message));
                }
                continue;
            }
            resolved.Add(await RenderOneAsync(assertion, cascade, secretNames, ct).ConfigureAwait(false));
        }
        return resolved;
    }

    private async ValueTask<ResolvedAssert> RenderOneAsync(
        AssertSpec assertion,
        IReadOnlyDictionary<string, string> cascade,
        IReadOnlySet<string> secretNames,
        CancellationToken ct)
    {
        var selector = assertion.Selector is null
            ? null
            : await Interpolation.ExpandAsync(assertion.Selector, cascade, registry, ct).ConfigureAwait(false);

        var secret = false;
        string? expected = null;
        if (assertion.Expected is not null)
        {
            (expected, secret) = await ExpandTrackingSecretsAsync(assertion.Expected, cascade, secretNames, ct)
                .ConfigureAwait(false);
        }

        IReadOnlyList<string>? expectedList = null;
        if (assertion.ExpectedList is { Count: > 0 } list)
        {
            var expandedList = new List<string>(list.Count);
            foreach (var item in list)
            {
                var (value, itemSecret) = await ExpandTrackingSecretsAsync(item, cascade, secretNames, ct)
                    .ConfigureAwait(false);
                expandedList.Add(value);
                secret |= itemSecret;
            }
            expectedList = expandedList;
        }

        return new ResolvedAssert(
            assertion with { Selector = selector, Expected = expected, ExpectedList = expectedList },
            secret);
    }

    /// <summary>Expands a template and reports whether anything secret went into it — either a
    /// cascade variable declared <c>secret: true</c> or a provider lookup the registry flagged.</summary>
    private async ValueTask<(string Value, bool Secret)> ExpandTrackingSecretsAsync(
        string input,
        IReadOnlyDictionary<string, string> cascade,
        IReadOnlySet<string> secretNames,
        CancellationToken ct)
    {
        var traceBefore = registry.Trace.Count;
        var value = await Interpolation.ExpandAsync(input, cascade, registry, ct).ConfigureAwait(false);

        foreach (var name in Interpolation.ReferencedNames(input))
        {
            if (secretNames.Contains(name)) return (value, true);
        }

        for (var i = traceBefore; i < registry.Trace.Count; i++)
        {
            if (registry.Trace[i].IsSecret) return (value, true);
        }

        return (value, false);
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
