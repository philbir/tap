using Tap.Studio.Auth;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Studio.Variables;
using Tap.Workspace;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;
using Tap.Execution.Workspace;
using Tap.Execution.Auth;
using Tap.Execution.Variables;

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
public sealed class WorkspaceService : IWorkspaceHost, IDisposable
{
    private readonly ILogger<WorkspaceService> _logger;
    private readonly KnownWorkspaceStore _knownWorkspaces;
    private readonly WorkspaceLoader _loader = new();
    private readonly Lock _gate = new();
    private readonly ProviderRegistryBuilder _registryBuilder;
    private readonly SystemSettingsStore _systemSettings;
    private readonly AuthTokenStore _tokens;
    private readonly StudioOptions _options;
    private readonly CachedAuthTokenSource _tokenSource;
    private readonly RequestPipeline _pipeline;
    private FileSystemWatcher? _watcher;
    private string _root;
    private LoadedWorkspace _workspace;
    private Timer? _debounce;

    public WorkspaceService(
        KnownWorkspaceStore knownWorkspaces,
        ILogger<WorkspaceService> logger,
        ProviderRegistryBuilder registryBuilder,
        SystemSettingsStore systemSettings,
        AuthTokenStore tokens,
        StudioOptions options)
    {
        _knownWorkspaces = knownWorkspaces;
        _logger = logger;
        _registryBuilder = registryBuilder;
        _systemSettings = systemSettings;
        _tokens = tokens;
        _options = options;

        // The pin. Normally the boot root is whatever the user last activated, which is right
        // for a tool they own — but an AppHost-launched Studio must open the folder the AppHost
        // named, every time, on every machine. Without this, a developer who once opened a
        // different workspace on this machine gets that one instead, and the AppHost's
        // WithWorkspaceFolder silently does nothing. The known-workspace list is left alone;
        // this only decides where *this* process starts.
        _root = options.IsWorkspacePinned ? options.WorkspaceRoot : knownWorkspaces.ActivePath;
        if (options.IsWorkspacePinned) Directory.CreateDirectory(_root);

        _workspace = Load(_root);
        _watcher = StartWatcher(_root);
        _tokenSource = new CachedAuthTokenSource(tokens, () => RootDirectory);
        _pipeline = new RequestPipeline(this);
    }

    public LoadedWorkspace Current
    {
        get { lock (_gate) return _workspace; }
    }

    // --- IWorkspaceHost -------------------------------------------------------------------
    // The engine sees a workspace, a registry factory, and a token source. Everything else
    // this class does — watching, switching, saving, the known-workspace list — is Studio's
    // business and deliberately invisible from there.

    LoadedWorkspace IWorkspaceHost.Workspace => Current;

    IAuthTokenSource IWorkspaceHost.Tokens => _tokenSource;

    /// <summary>The render path, shared with the CLI. Built once — it is stateless over the
    /// host, and the host re-reads its workspace on every access.</summary>
    public RequestPipeline Pipeline => _pipeline;

    public string RootDirectory
    {
        get { lock (_gate) return _root; }
    }

    /// <summary>How this Studio is hosted. Surfaced to the UI so it can lock the switcher
    /// and badge the header rather than offering an action that will 409.</summary>
    public StudioMode Mode => _options.Mode;

    public bool IsWorkspacePinned => _options.IsWorkspacePinned;

    /// <summary>Builds a fresh <see cref="VariableProviderRegistry"/> for the current
    /// workspace. Each call returns an independent instance with its own resolution cache
    /// and trace — appropriate for one render / one /api/variables request.
    ///
    /// <para><paramref name="envPath"/> selects the environment whose provider binding
    /// (default provider, aliases, strict flag) applies; <c>null</c> falls back to the
    /// manifest's default env — the same fallback the renderer uses for the vars cascade,
    /// so bare-token resolution and the cascade always agree on the active env.</para></summary>
    public VariableProviderRegistry CreateRegistry(string? envPath = null)
    {
        lock (_gate)
        {
            EnvFile? env = null;
            if (envPath is not null)
                env = _workspace.FindByPath(envPath) as EnvFile;
            else if (_workspace.Manifest?.DefaultEnv is { } defaultRef)
                env = _workspace.Resolve(defaultRef) as EnvFile;
            return CreateRegistryLocked(env);
        }
    }

    /// <summary>Overload for callers that already resolved the <see cref="EnvFile"/> (the
    /// render path). Passing <c>null</c> here means "no env binding" — unlike the path
    /// overload it does not fall back to the manifest default, because the caller already
    /// applied that fallback itself.</summary>
    public VariableProviderRegistry CreateRegistry(EnvFile? env)
    {
        lock (_gate)
        {
            return CreateRegistryLocked(env);
        }
    }

    private VariableProviderRegistry CreateRegistryLocked(EnvFile? env)
        => _registryBuilder.Build(
            _workspace,
            _root,
            _systemSettings.GetProviderConfigs(),
            _systemSettings.DefaultVariableProvider,
            env);

    public event Action? Changed;

    /// <summary>Swap to a different workspace root. Rebuilds the file watcher and reloads.
    /// Fires <see cref="Changed"/> so SSE listeners reload the UI.</summary>
    public void SwitchTo(string newRoot)
    {
        if (_options.IsWorkspacePinned)
        {
            throw new InvalidOperationException(
                "This Studio is hosted by an Aspire AppHost and is pinned to its workspace folder. "
                + "Change it in the AppHost's WithWorkspaceFolder(...) call.");
        }

        var canonical = Path.GetFullPath(newRoot);
        FileSystemWatcher? oldWatcher;
        lock (_gate)
        {
            if (string.Equals(_root, canonical, StringComparison.Ordinal)) return;
            oldWatcher = _watcher;
            _root = canonical;
            _workspace = Load(canonical);
            _watcher = StartWatcher(canonical);
            _knownWorkspaces.Activate(canonical);
        }
        oldWatcher?.Dispose();
        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the workspace, degrading a failure into an empty workspace that carries the reason.
    /// The alternative — letting it throw — kills the process during boot (this runs from
    /// <see cref="StudioHost.Build"/>), and a desktop shell then has nothing to show but an exit
    /// code. An empty-with-errors workspace still renders, and the user can switch roots from the
    /// UI, which is the fix for every case that lands here.
    /// </summary>
    private LoadedWorkspace Load(string root)
    {
        try
        {
            return _loader.Load(root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the workspace at {Root}", root);
            return new LoadedWorkspace(root, root, [], [
                new WorkspaceError(
                    WorkspaceErrorCode.E_WORKSPACE_LOAD_FAILED,
                    $"Could not read the workspace at '{root}': {ex.Message}")
            ]);
        }
    }

    /// <summary>Starts the reload watcher, or returns null when the OS won't give us one —
    /// a root on a filesystem without change notifications, or past the per-user watch limit.
    /// Losing live reload is a papercut; failing the boot over it is not.</summary>
    private FileSystemWatcher? StartWatcher(string root)
    {
        try
        {
            // Filters (plural) rather than the single-pattern constructor: the loader reads both
            // extension families, so the watcher has to see both or edits to half the workspace
            // would silently not reload.
            var w = new FileSystemWatcher(root)
            {
                Filters =
                {
                    "*" + KindResolver.CanonicalExtension,
                    "*" + KindResolver.LegacyExtension,
                    "*" + KindResolver.HttpExtension,
                },
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File watching is unavailable for {Root}; edits will not auto-reload", root);
            return null;
        }
    }

    /// <param name="templateOverrides">Overrides whose values are templates — a flow step's
    /// <c>vars:</c>, which have to see what earlier steps bound. See
    /// <see cref="WorkspaceRenderer.RenderAsync"/>.</param>
    /// <param name="draftSource">Unsaved raw text of the <c>.http</c> file
    /// <paramref name="requestPath"/> points into. See <see cref="ResolveRequestFile"/>.</param>
    public ValueTask<ResolvedRequest> RenderAsync(string requestPath, string? envPath,
        IReadOnlyDictionary<string, string>? overrides, CancellationToken ct,
        string? stageName = null, RequestSpecDto? draftSpec = null,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides = null,
        string? draftSource = null)
    {
        // Resolving an unsaved editor draft is the one part of the render path that is purely
        // Studio's — the engine only ever sees a RequestFile.
        var req = ResolveRequestFile(Current, requestPath, draftSpec, draftSource);
        return _pipeline.RenderAsync(req, envPath, overrides, ct, stageName, templateOverrides);
    }


    /// <summary>
    /// Expands a set of assertions against the scope a request would execute in, without
    /// building or sending the request. Backs <c>POST /api/assertions/evaluate</c>, which
    /// re-checks edited assertions against a response the client already has.
    /// </summary>
    public ValueTask<IReadOnlyList<ResolvedAssert>> RenderAssertionsAsync(
        IReadOnlyList<AssertSpec> assertions, string? requestPath, string? envPath, string? stageName,
        CancellationToken ct, bool tolerant = false,
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides = null)
        => _pipeline.RenderAssertionsAsync(
            assertions, requestPath, envPath, stageName, ct, tolerant, overrides, templateOverrides);

    private static EnvFile? ResolveEnv(LoadedWorkspace ws, string? envPath)
    {
        if (envPath is not null)
        {
            if (ws.FindByPath(envPath) is not EnvFile e)
                throw new FileNotFoundException($"Env '{envPath}' not in workspace.");
            return e;
        }
        return ws.Manifest?.DefaultEnv is { } defaultRef ? ws.Resolve(defaultRef) as EnvFile : null;
    }

    /// <summary>
    /// Resolve the <see cref="RequestFile"/> to render/execute. When <paramref name="draftSpec"/>
    /// is supplied (an unsaved editor draft), the request is built in-memory through the same
    /// emit pipeline as Save — <see cref="RequestSpecEmitter.ToFileSource"/> + <see cref="FileParser"/>
    /// — but never written to disk. Otherwise the on-disk file at <paramref name="requestPath"/> is used.
    /// Either way the request keeps its workspace-relative path, so auth/collection/stage resolution
    /// in the renderer behaves identically to the saved file.
    ///
    /// <para><paramref name="draftSource"/> is the same idea for a <c>.http</c> file, which has no
    /// spec to emit — the raw text <em>is</em> the document, so the draft arrives as text and is
    /// parsed rather than emitted-then-parsed.</para>
    /// </summary>
    private static RequestFile ResolveRequestFile(
        LoadedWorkspace ws, string requestPath, RequestSpecDto? draftSpec, string? draftSource = null)
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
        if (draftSource is not null)
            return HttpDraftResolver.Resolve(requestPath, draftSource);
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
            if (WorkspacePaths.TryResolve(_root, combined, out var fullPath, out _)
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
    /// rejects <c>.</c> segments, so they have to come off before <see cref="WorkspacePaths.TryResolve"/>.</summary>
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
    private ResolvedRequest InjectAuthToken(LoadedWorkspace ws, RequestFile req, ResolvedRequest rendered, string? envPath)
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

        // A profile has one cached token per stage and env — read the one minted for the stage
        // and env this render resolved to, not whichever entry happens to exist.
        var scope = AuthScopeResolver
            .ContextFor(ws, auth.RelativePath, req.RelativePath, rendered.Metadata.StageName, envPath)
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
    /// stage caches its own token. <paramref name="envPath"/> is the selected env, which caches
    /// its own token for every profile — pass the same one the render used or the Flow tab
    /// reports on an entry the executor will never read.</para>
    /// </summary>
    public AuthStatusDto BuildAuthStatus(
        string requestPath, RequestSpecDto? draftSpec = null, string? stageName = null, string? envPath = null,
        string? draftSource = null)
    {
        var ws = Current;
        RequestFile req;
        try { req = ResolveRequestFile(ws, requestPath, draftSpec, draftSource); }
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
            .ContextFor(ws, auth.RelativePath, req.RelativePath, stageName, envPath)
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

        var collection = CollectionLocator.ForRequest(ws, req);
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
        if (!WorkspacePaths.TryResolve(RootDirectory, relativePath, out var full, out var err))
            throw new InvalidOperationException(err);
        return File.ReadAllText(full);
    }

    /// <summary>
    /// Writes one workspace file, parse-validating it first so a malformed save is rejected
    /// before it lands on disk. Returns the path actually written, which differs from
    /// <paramref name="relativePath"/> only when the caller asked for a canonical <c>.tap</c>
    /// name and the file already exists under the legacy extension — see
    /// <see cref="LoadedWorkspace.ResolveWritePath"/>.
    /// </summary>
    public string Save(string relativePath, string content)
    {
        var target = Current.ResolveWritePath(relativePath);

        if (!WorkspacePaths.TryResolve(RootDirectory, target, out var full, out var err))
            throw new InvalidOperationException(err);

        var fileName = Path.GetFileName(target);
        if (KindResolver.Resolve(fileName) is not { } match)
            throw new InvalidOperationException($"'{target}' is not a recognized Tap file suffix.");

        // A .http file has no frontmatter and holds N requests, so FileParser — whose whole job is
        // enforcing the frontmatter-and-one-kind contract — would reject every valid one. Validate
        // it with its own parser instead, and surface the first hard error so the editor can put a
        // marker on the right line. Warnings (foreign constructs) must NOT block a save: the file
        // may legitimately be shared with another tool.
        if (match.IsHttpFile)
        {
            var parsed = HttpFileParser.Parse(target, content);
            var failure = parsed.Errors.FirstOrDefault(e => e.Severity == WorkspaceErrorSeverity.Error);
            if (failure is not null) throw new WorkspaceParseException(failure);
        }
        else
        {
            FileParser.Parse(target, content);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return target;
    }

    /// <summary>Synchronously reload the workspace model from disk. For endpoints that
    /// mutate the filesystem and must serve the fresh model to the very next request —
    /// the watcher's debounced reload is too slow for a client that refetches immediately.
    /// Doesn't fire <see cref="Changed"/>; the watcher still does that once it catches up.</summary>
    public void ReloadNow()
    {
        var fresh = Load(RootDirectory);
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
        _watcher?.Dispose();
    }
}
