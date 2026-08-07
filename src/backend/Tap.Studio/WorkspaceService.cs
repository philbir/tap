using Tap.Studio.Auth;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Studio.Variables;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;

namespace Tap.Studio;

/// <summary>
/// Owns the loaded <see cref="Workspace"/> in-process. Reload happens on any FS change under
/// the selected workspace root. The watcher is debounced; rapid editor saves coalesce into
/// a single reload.
///
/// <para>The variable provider registry is rebuilt every time it's requested: providers can be
/// stateful (per-render resolution cache + trace), so callers get a fresh one per request
/// rather than sharing state across executions. The composition itself is cheap; factory
/// outputs (e.g. azkv's SecretClient) are not pooled, which is acceptable for desktop-scale
/// traffic — revisit when shared infra calls for it.</para>
/// </summary>
public sealed class WorkspaceService : IDisposable
{
    private readonly ILogger<WorkspaceService> _logger;
    private readonly KnownWorkspaceStore _knownWorkspaces;
    private readonly WorkspaceLoader _loader = new();
    private readonly Lock _gate = new();
    private readonly ProviderRegistryBuilder _registryBuilder;
    private readonly SystemSettingsStore _systemSettings;
    private readonly AuthTokenStore _tokens;
    private FileSystemWatcher _watcher;
    private string _root;
    private LoadedWorkspace _workspace;
    private Timer? _debounce;

    public WorkspaceService(
        KnownWorkspaceStore knownWorkspaces,
        ILogger<WorkspaceService> logger,
        ProviderRegistryBuilder registryBuilder,
        SystemSettingsStore systemSettings,
        AuthTokenStore tokens)
    {
        _knownWorkspaces = knownWorkspaces;
        _logger = logger;
        _registryBuilder = registryBuilder;
        _systemSettings = systemSettings;
        _tokens = tokens;
        _root = knownWorkspaces.ActivePath;
        _workspace = _loader.Load(_root);
        _watcher = StartWatcher(_root);
    }

    public LoadedWorkspace Current
    {
        get { lock (_gate) return _workspace; }
    }

    public string RootDirectory
    {
        get { lock (_gate) return _root; }
    }

    /// <summary>Builds a fresh <see cref="VariableProviderRegistry"/> for the current
    /// workspace. Each call returns an independent instance with its own resolution cache
    /// and trace — appropriate for one render / one /api/variables request.</summary>
    public VariableProviderRegistry CreateRegistry()
    {
        lock (_gate)
        {
            return _registryBuilder.Build(
                _workspace,
                _root,
                _systemSettings.GetProviderConfigs(),
                _systemSettings.DefaultVariableProvider);
        }
    }

    public event Action? Changed;

    /// <summary>Swap to a different workspace root. Rebuilds the file watcher and reloads.
    /// Fires <see cref="Changed"/> so SSE listeners reload the UI.</summary>
    public void SwitchTo(string newRoot)
    {
        var canonical = Path.GetFullPath(newRoot);
        FileSystemWatcher oldWatcher;
        lock (_gate)
        {
            if (string.Equals(_root, canonical, StringComparison.Ordinal)) return;
            oldWatcher = _watcher;
            _root = canonical;
            _workspace = _loader.Load(canonical);
            _watcher = StartWatcher(canonical);
            _knownWorkspaces.Activate(canonical);
        }
        oldWatcher.Dispose();
        Changed?.Invoke();
    }

    private FileSystemWatcher StartWatcher(string root)
    {
        var tapDir = root;
        var w = new FileSystemWatcher(tapDir, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        w.Changed += OnFsChange;
        w.Created += OnFsChange;
        w.Deleted += OnFsChange;
        w.Renamed += OnFsChange;
        return w;
    }

    public async ValueTask<ResolvedRequest> RenderAsync(string requestPath, string? envPath,
        IReadOnlyDictionary<string, string>? overrides, CancellationToken ct,
        string? stageName = null, RequestSpecDto? draftSpec = null)
    {
        var ws = Current;
        var req = ResolveRequestFile(ws, requestPath, draftSpec);

        EnvFile? env = null;
        if (envPath is not null)
        {
            if (ws.FindByPath(envPath) is not EnvFile e)
                throw new FileNotFoundException($"Env '{envPath}' not in workspace.");
            env = e;
        }
        else if (ws.Manifest?.DefaultEnv is { } defaultRef)
        {
            env = ws.Resolve(defaultRef) as EnvFile;
        }

        var registry = CreateRegistry();
        var renderer = new WorkspaceRenderer(ws, registry);
        var rendered = await renderer.RenderAsync(req, env, overrides, ct, stageName).ConfigureAwait(false);
        rendered = ResolveBinaryRef(req, rendered);
        return InjectAuthToken(ws, req, rendered);
    }

    /// <summary>
    /// Resolve the <see cref="RequestFile"/> to render/execute. When <paramref name="draftSpec"/>
    /// is supplied (an unsaved editor draft), the request is built in-memory through the same
    /// emit pipeline as Save — <see cref="RequestSpecEmitter.ToFileSource"/> + <see cref="FileParser"/>
    /// — but never written to disk. Otherwise the on-disk file at <paramref name="requestPath"/> is used.
    /// Either way the request keeps its workspace-relative path, so auth/collection/stage resolution
    /// in the renderer behaves identically to the saved file.
    /// </summary>
    private static RequestFile ResolveRequestFile(LoadedWorkspace ws, string requestPath, RequestSpecDto? draftSpec)
    {
        if (draftSpec is not null)
        {
            var source = RequestSpecEmitter.ToFileSource(draftSpec);
            if (FileParser.Parse(draftSpec.Path, source) is not RequestFile parsed)
                throw new WorkspaceParseException(new WorkspaceError(
                    WorkspaceErrorCode.E_KIND_MISMATCH,
                    $"Draft '{draftSpec.Path}' did not parse as a request.",
                    draftSpec.Path));
            return parsed;
        }
        if (ws.FindByPath(requestPath) is not RequestFile req)
            throw new FileNotFoundException($"Request '{requestPath}' not in workspace.");
        return req;
    }

    /// <summary>If the rendered body is a single-line <c>&lt; ./path</c> file reference,
    /// load the file's bytes off disk and attach them as <see cref="ResolvedRequest.BinaryBody"/>.
    /// The string body is left intact so the request-capture view can still show what was
    /// referenced. Refs are resolved relative to the request file's directory inside
    /// <c>.tap/</c>; anything that escapes the workspace is rejected before File.ReadAllBytes
    /// sees the path.</summary>
    private ResolvedRequest ResolveBinaryRef(RequestFile req, ResolvedRequest rendered)
    {
        if (TryParseBinaryRef(rendered.Body, out var relPath))
        {
            var requestDir = Path.GetDirectoryName(req.RelativePath) ?? string.Empty;
            // Compose the request-dir-relative ref into a workspace-relative path so we can
            // route it through the same path resolver that everything else uses. Reject
            // refs that escape the workspace; let the executor render the body verbatim
            // (i.e. send the literal "< …" string) so the failure mode is visible.
            var combined = string.IsNullOrEmpty(requestDir) ? relPath : $"{requestDir}/{relPath}";
            if (WorkspacePathResolver.TryResolve(_root, combined, out var fullPath, out _)
                && File.Exists(fullPath))
            {
                var bytes = File.ReadAllBytes(fullPath);
                return rendered with { BinaryBody = bytes };
            }
        }
        return rendered;
    }

    /// <summary>Match <c>&lt; ./path</c> or <c>&lt; path</c> as the entire body. Whitespace
    /// trimmed. Returns the path with any leading <c>./</c> stripped — the resolver
    /// rejects <c>.</c> segments, so they have to come off before <see cref="WorkspacePathResolver.TryResolve"/>.</summary>
    private static bool TryParseBinaryRef(string? body, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.Trim();
        if (!trimmed.StartsWith("< ")) return false;
        // A body of "< ./foo.png\nGarbage" isn't a ref — must be a one-liner.
        if (trimmed.IndexOfAny(['\r', '\n']) >= 0) return false;
        var rest = trimmed[2..].Trim();
        if (rest.StartsWith("./")) rest = rest[2..];
        if (rest.Length == 0) return false;
        relativePath = rest;
        return true;
    }

    /// <summary>
    /// Stamps a <c>Authorization: Bearer …</c> header on dynamic-auth requests (oauth2,
    /// azure-cli, jwt, github with a mode that needs a runtime exchange) by looking up the
    /// cached token for the resolved auth profile. The renderer can't do this itself because
    /// <see cref="Tap.Workspace"/> doesn't depend on the Studio's <see cref="AuthTokenStore"/>.
    ///
    /// <para>For <c>github</c> this attaches the bearer header so PAT, gh-cli, App, and OAuth
    /// modes all produce identical request shapes — matching the header
    /// <see cref="AuthRunner.WithGithubExtras"/> attaches when the profile is executed
    /// standalone.</para>
    ///
    /// <para>Without this, profiles whose token comes from a runtime exchange (i.e. anything
    /// past <c>bearer</c>/<c>basic</c>/<c>apiKey</c>/<c>custom</c>) would render without an
    /// Authorization header — the renderer is static, so it can't see the cached token.</para>
    /// </summary>
    private ResolvedRequest InjectAuthToken(LoadedWorkspace ws, RequestFile req, ResolvedRequest rendered)
    {
        var auth = ResolveAuth(ws, req, rendered.Metadata.StageName);
        if (auth is null || auth.Type is "none" or "basic" or "bearer" or "apiKey" or "custom" or "aws-sigv4")
            return rendered;

        // Github PAT mode is already handled by the renderer's static branch; the other modes
        // and every oauth2/azure-cli/jwt profile fall through to the cached-token lookup.
        if (auth.Type == "github")
        {
            var mode = (auth.Fields.GetValueOrDefault("mode") ?? "pat").Trim();
            if (mode is "pat" or "")
                return rendered;
        }

        // A profile that lives inside a collection has one cached token per stage — read the
        // one minted for the stage this render resolved to.
        var scope = AuthScopeResolver
            .ContextFor(ws, auth.RelativePath, req.RelativePath, rendered.Metadata.StageName)
            .ScopeFor(auth.RelativePath);
        var cached = _tokens.Get(_root, scope);
        if (cached is null || string.IsNullOrEmpty(cached.AccessToken))
            return rendered;

        var headers = new Dictionary<string, string>(rendered.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + cached.AccessToken,
        };
        return rendered with { Headers = headers };
    }

    /// <summary>
    /// Snapshot the auth state the executor would see for <paramref name="requestPath"/>:
    /// which profile is bound (via request &lt; stage &lt; collection precedence), whether a
    /// usable runtime token is cached, and whether running the flow would be interactive.
    /// The Flow tab uses this to decide between "already authorized" vs "Run auth" affordances.
    ///
    /// <para><paramref name="stageName"/> is the stage the caller has selected. It only changes
    /// the answer for a profile that lives inside the request's own collection, where each
    /// stage caches its own token.</para>
    /// </summary>
    public AuthStatusDto BuildAuthStatus(string requestPath, RequestSpecDto? draftSpec = null, string? stageName = null)
    {
        var ws = Current;
        RequestFile req;
        try { req = ResolveRequestFile(ws, requestPath, draftSpec); }
        catch (Exception ex) when (ex is FileNotFoundException or WorkspaceParseException)
        {
            return new AuthStatusDto(Path: null, Type: null, Source: "none", Interactive: false, ExpiresAt: null);
        }

        var auth = ResolveAuth(ws, req, stageName);
        if (auth is null)
            return new AuthStatusDto(Path: null, Type: null, Source: "none", Interactive: false, ExpiresAt: null);

        // Profiles whose auth contribution is built inline at render time — no runtime
        // token to acquire, no flow to run.
        if (auth.Type is "basic" or "bearer" or "apiKey" or "custom" or "aws-sigv4" or "none")
            return new AuthStatusDto(auth.RelativePath, auth.Type, Source: "static", Interactive: false, ExpiresAt: null);

        // Github PAT mode is functionally static — the renderer stamps the token from the
        // profile fields without touching the token store.
        if (auth.Type == "github")
        {
            var mode = (auth.Fields.GetValueOrDefault("mode") ?? "pat").Trim();
            if (mode is "pat" or "")
                return new AuthStatusDto(auth.RelativePath, auth.Type, Source: "static", Interactive: false, ExpiresAt: null);
        }

        var interactive = IsInteractive(auth);
        var scope = AuthScopeResolver
            .ContextFor(ws, auth.RelativePath, req.RelativePath, stageName)
            .ScopeFor(auth.RelativePath);
        var cached = _tokens.Get(_root, scope);
        if (cached is null || string.IsNullOrEmpty(cached.AccessToken))
            return new AuthStatusDto(auth.RelativePath, auth.Type, Source: "missing", interactive, ExpiresAt: null);

        // Mirror AuthRunner's freshness check (30s slack) so the Flow tab agrees with what the
        // runner would do on re-execute.
        if (cached.ExpiresAt is not null && cached.ExpiresAt <= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30))
            return new AuthStatusDto(auth.RelativePath, auth.Type, Source: "expired", interactive, cached.ExpiresAt);

        return new AuthStatusDto(auth.RelativePath, auth.Type, Source: "cached", interactive, cached.ExpiresAt);
    }

    /// <summary>True when running the profile's auth flow may open a browser window or
    /// device-code prompt. Drives the "Run auth" button label and prominence.</summary>
    private static bool IsInteractive(AuthFile auth)
    {
        switch (auth.Type)
        {
            case "oauth2":
                var flow = (auth.Fields.GetValueOrDefault("flow")
                            ?? auth.Fields.GetValueOrDefault("grantType")
                            ?? "authorization_code_pkce").Trim();
                return flow is "authorization_code" or "authorization_code_pkce" or "device_code" or "device";
            case "github":
                return (auth.Fields.GetValueOrDefault("mode") ?? "pat").Trim() == "oauth";
            default:
                return false;
        }
    }

    /// <summary>
    /// Mirrors <see cref="WorkspaceRenderer"/>'s auth resolution order — request &lt; stage &lt; collection —
    /// so the token injector targets the same file the renderer's static <c>ApplyAuthHeaders</c>
    /// saw. <paramref name="stageName"/> is the stage the render resolved to; null falls back
    /// to the collection's default stage, same as the renderer. Returning null means there's
    /// no auth attached (or the ref didn't resolve).
    /// </summary>
    private static AuthFile? ResolveAuth(LoadedWorkspace ws, RequestFile req, string? stageName)
    {
        var requestDir = Path.GetDirectoryName(req.RelativePath) ?? string.Empty;
        if (ws.Resolve(req.Auth, requestDir) is AuthFile direct) return direct;

        var collection = CollectionLocator.ForFile(ws, req.RelativePath);
        if (collection is null) return null;
        var collectionDir = Path.GetDirectoryName(collection.RelativePath) ?? string.Empty;
        var stage = collection.FindStage(stageName) ?? collection.FindStage(collection.DefaultStage);
        if (stage?.DefaultAuth is { } stageAuthRef && ws.Resolve(stageAuthRef, collectionDir) is AuthFile stageAuth)
            return stageAuth;
        if (collection.DefaultAuth is { } collectionAuthRef && ws.Resolve(collectionAuthRef, collectionDir) is AuthFile collectionAuth)
            return collectionAuth;
        return null;
    }

    /// <summary>Reads the raw source text of a workspace file. Used by detail endpoints so
    /// the UI can offer a "Source" tab for raw-markdown editing. The path goes through the
    /// shared <see cref="WorkspacePathResolver"/> so anything outside <c>.tap/</c> is rejected
    /// before <see cref="File.ReadAllText(string)"/> sees it.</summary>
    public string ReadSource(string relativePath)
    {
        if (!WorkspacePathResolver.TryResolve(RootDirectory, relativePath, out var full, out var err))
            throw new InvalidOperationException(err);
        return File.ReadAllText(full);
    }

    public void Save(string relativePath, string content)
    {
        if (!WorkspacePathResolver.TryResolve(RootDirectory, relativePath, out var full, out var err))
            throw new InvalidOperationException(err);

        var fileName = Path.GetFileName(relativePath);
        if (KindResolver.FromFileName(fileName) is null)
            throw new InvalidOperationException($"'{relativePath}' is not a recognized Tap file suffix.");

        FileParser.Parse(relativePath, content);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Synchronously reload the workspace model from disk. For endpoints that
    /// mutate the filesystem and must serve the fresh model to the very next request —
    /// the watcher's debounced reload is too slow for a client that refetches immediately.
    /// Doesn't fire <see cref="Changed"/>; the watcher still does that once it catches up.</summary>
    public void ReloadNow()
    {
        var fresh = _loader.Load(RootDirectory);
        lock (_gate) _workspace = fresh;
    }

    private void OnFsChange(object _, FileSystemEventArgs __)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            try
            {
                var root = RootDirectory;
                var fresh = _loader.Load(root);
                lock (_gate)
                {
                    _workspace = fresh;
                }
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace reload failed");
            }
        }, null, 150, Timeout.Infinite);
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        _watcher.Dispose();
    }
}
