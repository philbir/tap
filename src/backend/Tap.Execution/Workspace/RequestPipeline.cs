using Tap.Execution.Auth;
using Tap.Workspace;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Workspace;

/// <summary>
/// Turns a <see cref="RequestFile"/> into a <see cref="ResolvedRequest"/> ready for the wire:
/// resolves the environment, builds the variable registry, runs the renderer, attaches a
/// referenced file body, and stamps a runtime-acquired bearer token.
///
/// <para>Extracted from the Studio's workspace service so a CLI run and a Send from the UI go
/// through the same five steps in the same order. Anything above this — reload policy, unsaved
/// editor drafts, the known-workspace list — stays with the host.</para>
/// </summary>
public sealed class RequestPipeline(IWorkspaceHost host)
{
    public IWorkspaceHost Host => host;

    public LoadedWorkspace Workspace => host.Workspace;

    /// <param name="overrides">Literal values that outrank every file scope — the per-run
    /// variable tier.</param>
    /// <param name="templateOverrides">Overrides whose values are themselves templates,
    /// expanded in order against everything below them. A flow step's <c>vars:</c> arrive
    /// here.</param>
    /// <param name="declaredOverrides">Which of <paramref name="overrides"/> came out of a
    /// <c>vars:</c> block — a test set's or a flow's own variables, as opposed to a value a
    /// step extracted from a response. Only those may hold a reference to another variable.</param>
    public async ValueTask<ResolvedRequest> RenderAsync(
        RequestFile request,
        string? envPath,
        IReadOnlyDictionary<string, string>? overrides,
        CancellationToken ct,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides = null,
        IReadOnlySet<string>? declaredOverrides = null)
    {
        var env = ResolveEnv(envPath);
        var renderer = new WorkspaceRenderer(host.Workspace, host.CreateRegistry(env));
        var rendered = await renderer
            .RenderAsync(request, env, overrides, ct, templateOverrides, declaredOverrides)
            .ConfigureAwait(false);

        rendered = ResolveBinaryRef(request, rendered);
        // The renderer reports the env that actually applied — a scoped env out of range for this
        // request's collection drops out there, and the token has to be keyed the same way or the
        // send would carry a token minted under an env the request never saw.
        return await InjectAuthTokenAsync(request, rendered, rendered.Metadata.EnvPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Expands a set of assertions against the scope a request would execute in, without
    /// building or sending it. Backs the Studio's live re-check and a step's extra assertions.
    /// </summary>
    public async ValueTask<IReadOnlyList<ResolvedAssert>> RenderAssertionsAsync(
        IReadOnlyList<AssertSpec> assertions,
        string? requestPath,
        string? envPath,
        CancellationToken ct,
        bool tolerant = false,
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyList<KeyValuePair<string, string>>? templateOverrides = null,
        IReadOnlySet<string>? declaredOverrides = null)
    {
        if (assertions.Count == 0) return [];

        // An unsaved request has no file yet, but the *path* is enough to place it in the right
        // collection — which is what the cascade resolves from.
        var request = requestPath is not null && host.Workspace.FindByPath(requestPath) is RequestFile found
            ? found
            : new RequestFile { Kind = WorkspaceKind.Request, RelativePath = requestPath ?? "unsaved" + KindResolver.SuffixFor(WorkspaceKind.Request) };

        var env = ResolveEnv(envPath);
        var renderer = new WorkspaceRenderer(host.Workspace, host.CreateRegistry(env));
        return await renderer
            .RenderAssertionsAsync(
                request, env, assertions, ct, tolerant, overrides, templateOverrides, declaredOverrides)
            .ConfigureAwait(false);
    }

    /// <summary>The caller's env when it named one, the manifest's <c>defaultEnv</c> when it
    /// didn't. A named env that isn't in the workspace is an error rather than a silent
    /// fallback — resolving against variables nobody selected is the failure worth shouting
    /// about.</summary>
    public EnvFile? ResolveEnv(string? envPath)
    {
        if (envPath is not null)
        {
            if (host.Workspace.FindByPath(envPath) is not EnvFile env)
                throw new FileNotFoundException($"Env '{envPath}' is not in the workspace.");
            return env;
        }
        return host.Workspace.Manifest?.DefaultEnv is { } defaultRef
            ? host.Workspace.Resolve(defaultRef) as EnvFile
            : null;
    }

    /// <summary>
    /// If the rendered body is a single-line <c>&lt; ./path</c> file reference, load the bytes
    /// off disk and attach them. The string body is left intact so a request-capture view can
    /// still show what was referenced. Refs resolve relative to the request file's directory;
    /// anything escaping the workspace is rejected before <c>File.ReadAllBytes</c> sees it.
    /// </summary>
    private ResolvedRequest ResolveBinaryRef(RequestFile request, ResolvedRequest rendered)
    {
        if (!TryParseBinaryRef(rendered.Body, out var relativePath)) return rendered;

        var requestDir = Path.GetDirectoryName(request.RelativePath) ?? string.Empty;
        var combined = string.IsNullOrEmpty(requestDir) ? relativePath : $"{requestDir}/{relativePath}";
        if (WorkspacePaths.TryResolve(host.RootDirectory, combined, out var fullPath, out _) && File.Exists(fullPath))
        {
            return rendered with { BinaryBody = File.ReadAllBytes(fullPath) };
        }
        // Let the executor send the literal "< …" string so the failure stays visible.
        return rendered;
    }

    /// <summary>Match <c>&lt; ./path</c> or <c>&lt; path</c> as the entire body.</summary>
    private static bool TryParseBinaryRef(string? body, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.Trim();
        if (!trimmed.StartsWith("< ", StringComparison.Ordinal)) return false;
        // A body of "< ./foo.png\nGarbage" isn't a ref — it must be a one-liner.
        if (trimmed.IndexOfAny(['\r', '\n']) >= 0) return false;
        var rest = trimmed[2..].Trim();
        if (rest.StartsWith("./", StringComparison.Ordinal)) rest = rest[2..];
        if (rest.Length == 0) return false;
        relativePath = rest;
        return true;
    }

    /// <summary>
    /// Stamps <c>Authorization: Bearer …</c> on requests whose auth profile needs a runtime
    /// exchange (oauth2, azure-cli, jwt, and github in any mode past PAT). The renderer can't do
    /// this itself: it is static, and the token comes from whatever the host decided a token
    /// source is.
    /// </summary>
    private async ValueTask<ResolvedRequest> InjectAuthTokenAsync(
        RequestFile request, ResolvedRequest rendered, string? envPath, CancellationToken ct)
    {
        var auth = ResolveAuth(host.Workspace, request, envPath);
        if (auth is null || !NeedsRuntimeToken(auth)) return rendered;

        // A profile has one token per env — read the one minted for the env this render resolved
        // to, not whichever entry happens to exist.
        var scope = AuthScopeResolver
            .ContextFor(host.Workspace, auth.RelativePath, envPath)
            .ScopeFor(auth.RelativePath);

        var token = await host.Tokens.GetAsync(auth, scope, ct).ConfigureAwait(false);
        if (token is not { AccessToken.Length: > 0 } minted) return rendered;

        var headers = new Dictionary<string, string>(rendered.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + minted.AccessToken,
        };
        // The minted token joins the redaction set: Authorization is masked by name anyway,
        // but the raw value must also never survive if it shows up anywhere else in an echo.
        return rendered with { Headers = headers, Redactor = rendered.Redactor.With(minted.AccessToken) };
    }

    /// <summary>True when the profile's Authorization header can only come from a runtime
    /// exchange. The inline kinds are built by the renderer and never consult a token source.</summary>
    public static bool NeedsRuntimeToken(AuthFile auth)
    {
        if (auth.Type is "none" or "basic" or "bearer" or "apiKey" or "custom" or "aws-sigv4") return false;

        // GitHub PAT mode is functionally static — the renderer stamps it from the profile fields.
        if (auth.Type == "github")
        {
            var mode = (auth.Fields.GetValueOrDefault("mode") ?? "pat").Trim();
            return mode is not ("pat" or "");
        }
        return true;
    }

    /// <summary>
    /// Mirrors the renderer's auth resolution order — request &lt; env &lt; collection — so the
    /// token injector targets the same profile the renderer's static pass saw.
    /// </summary>
    /// <param name="envPath">The environment in effect. Its <c>defaultAuth</c> only counts when
    /// the env is in scope for the request's collection, exactly as in the renderer.</param>
    public static AuthFile? ResolveAuth(LoadedWorkspace workspace, RequestFile request, string? envPath)
    {
        var requestDir = Path.GetDirectoryName(request.RelativePath) ?? string.Empty;
        if (workspace.Resolve(request.Auth, requestDir) is AuthFile direct) return direct;

        var collection = CollectionLocator.ForRequest(workspace, request);
        if (collection is null) return null;

        var slug = CollectionLocator.SlugForFile(collection.RelativePath);
        if (envPath is not null && workspace.FindByPath(envPath) is EnvFile env
            && env.BindingFor(slug)?.DefaultAuth is { } envRef
            && workspace.Resolve(envRef, Path.GetDirectoryName(env.RelativePath) ?? string.Empty) is AuthFile envAuth)
        {
            return envAuth;
        }

        var collectionDir = Path.GetDirectoryName(collection.RelativePath) ?? string.Empty;
        if (collection.DefaultAuth is { } collectionRef && workspace.Resolve(collectionRef, collectionDir) is AuthFile collectionAuth)
            return collectionAuth;
        return null;
    }
}
