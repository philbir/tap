using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Json;
using Tap.Studio.Auth;
using Tap.Studio.Contracts;
using Tap.Studio.Endpoints;
using Tap.Studio.Variables;
using Tap.Workspace.Security;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;
using Tap.Execution.Variables;
using Tap.Execution.Auth;

namespace Tap.Studio;

/// <summary>
/// Builds the Studio <see cref="WebApplication"/>. Kept separate from <c>Program.cs</c> the same
/// way <see cref="Tap.Server.TapInspectorHost"/> is — so the CLI can host the studio in-process
/// without spinning a second binary.
/// </summary>
public static class StudioHost
{
    public static WebApplication Build(string[] args, StudioOptions options)
    {
        StartupSignal.Progress("host.building", "Configuring the Studio host");

        // When the shell sets Studio__WebRoot (Tauri does this from the
        // bundled resource path), we point Kestrel's static-file handler at
        // that directory explicitly. PublishSingleFile makes
        // AppContext.BaseDirectory a temp extraction dir, so the default
        // ContentRoot/wwwroot lookup misses the SPA we just shipped.
        var webRoot = Environment.GetEnvironmentVariable("Studio__WebRoot");
        WebApplicationBuilder builder;
        if (!string.IsNullOrWhiteSpace(webRoot) && Directory.Exists(webRoot))
        {
            var contentRoot = Path.GetDirectoryName(Path.GetFullPath(webRoot))
                ?? AppContext.BaseDirectory;
            builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
                WebRootPath = webRoot,
            });
        }
        else
        {
            builder = WebApplication.CreateBuilder(args);
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (TryParseLoopback(options.Host, out var loopback))
                {
                    k.Listen(loopback, options.Port);
                }
                else if (IPAddress.TryParse(options.Host, out var ip))
                {
                    k.Listen(ip, options.Port);
                }
                else if (string.Equals(options.Host, "*", StringComparison.Ordinal)
                      || string.Equals(options.Host, "0.0.0.0", StringComparison.Ordinal)
                      || string.Equals(options.Host, "+", StringComparison.Ordinal))
                {
                    k.ListenAnyIP(options.Port);
                }
                else
                {
                    // Hostnames (e.g. "studio.local") aren't valid Kestrel bind targets;
                    // bind to all interfaces but defer the warning to startup.
                    k.ListenAnyIP(options.Port);
                }
            });
        }

        builder.Services.AddSingleton(options);
        // The settings store moved to Tap.Execution so a headless run reads the same
        // ~/.tap/system.json the UI edits; it takes its own options rather than the web host's.
        builder.Services.AddSingleton(new SystemSettingsOptions
        {
            SystemDir = options.SystemDir,
            SystemProviders = options.SystemProviders,
        });
        builder.Services.AddSingleton<SystemSettingsStore>();
        // One key source for the process, pinned to the same system dir as system.json, so
        // TAP_SYSTEM_DIR moves every piece of user-level state as a unit.
        builder.Services.AddSingleton<IEncryptionKeySource>(new MachineEncryptionKeySource(options.SystemDir));
        builder.Services.AddSingleton<Tap.Studio.Ai.AiProviderFactory>();
        builder.Services.AddSingleton<KnownWorkspaceStore>();
        builder.Services.AddSingleton<WorkspaceService>();
        builder.Services.AddSingleton<GitService>();

        // The agent surface as MCP tools at /mcp, over the live WorkspaceService — same
        // shared tool layer the CLI's stdio server hosts, but with this process's cached
        // interactive tokens, so an agent can run PKCE-authed requests the user signed
        // into without any credential leaving the Studio.
        builder.Services.AddSingleton<Mcp.IMcpWorkspaceProvider, Mcp.StudioMcpProvider>();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<Mcp.TapStudioTools>();
        builder.Services.AddHttpContextAccessor();

        // OpenAPI spec fetching. AllowAutoRedirect must stay off: HttpExecutionHelpers re-validates
        // every hop, and that per-hop check *is* the SSRF guard for a user-supplied spec URL. Let
        // the handler follow redirects itself and the guard never runs.
        builder.Services.AddHttpClient("openapi")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<OpenApi.OpenApiDocumentCache>();
        // Turns each Aspire-referenced API's OpenAPI document into real requests on first run.
        // A hosted service rather than part of the boot scaffold: that path is synchronous and
        // already the slowest thing in a cold start, and this one makes network calls.
        builder.Services.AddHostedService<AspireOpenApiScaffold>();
        builder.Services.AddSingleton<OpenApi.OpenApiSpecSource>(sp =>
            new OpenApi.OpenApiSpecSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient("openapi")));

        builder.Services.AddHttpClient("auth");
        builder.Services.AddSingleton<OidcDiscoveryClient>(sp =>
            new OidcDiscoveryClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("auth")));
        builder.Services.AddTransient<AuthRunner>(sp => new AuthRunner(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("auth"),
            sp.GetRequiredService<OidcDiscoveryClient>(),
            sp.GetRequiredService<AuthFlowStore>(),
            sp.GetRequiredService<AuthTokenStore>(),
            sp.GetRequiredService<WorkspaceService>(),
            sp.GetRequiredService<IHttpContextAccessor>(),
            sp.GetRequiredService<ILogger<AuthRunner>>()));
        builder.Services.AddSingleton<AuthFlowStore>();
        builder.Services.AddSingleton(new AuthTokenStoreOptions
        {
            Directory = Path.GetDirectoryName(options.StatePath) ?? Path.GetTempPath(),
        });
        builder.Services.AddSingleton<AuthTokenStore>();

        // Variable provider factories. Each registered factory adds support for one
        // `type:` in workspace + system provider config. Add a new factory here to
        // make a new provider type available everywhere.
        builder.Services.AddSingleton<IVariableProviderFactory, EnvVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, FileVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, AzureKeyVaultVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, OnePasswordVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, SystemVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, AspireVariableProviderFactory>();
        builder.Services.AddSingleton<ProviderRegistryBuilder>();
        builder.Services.AddSingleton<Tap.Studio.AzureDiscovery.AzureDiscoveryService>();

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, StudioJson.Default);
        });

        var app = builder.Build();

        // Load the workspace before anything can serve a request against it. This is the
        // slowest thing in a cold start by a wide margin — it walks the workspace folder —
        // so it announces the folder it is about to read. A desktop launch that appears to
        // hang is nearly always this line with a root nobody meant to open (see
        // StudioOptions.WorkspaceRoot); saying so up front is what makes that visible.
        // Scaffold before the first load, not after: the loader is what the UI serves, and a
        // workspace scaffolded afterwards would show up empty until the watcher caught up.
        if (options.IsWorkspacePinned)
        {
            try
            {
                var scaffold = AspireWorkspaceScaffold.Run(
                    options.WorkspaceRoot,
                    AspireWorkspaceScaffold.ReadApis(app.Configuration));

                if (!scaffold.IsNoOp)
                {
                    // Logged rather than silent: this writes files into the developer's repo, and
                    // the Aspire dashboard's resource console is where they will look for why.
                    app.Logger.LogInformation(
                        "Scaffolded {Count} file(s) into {Root}: {Files}",
                        scaffold.Created.Count, options.WorkspaceRoot, string.Join(", ", scaffold.Created));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only or unwritable folder is worth a warning, never a failed boot —
                // Studio is still perfectly usable against whatever is already there.
                app.Logger.LogWarning(ex, "Could not scaffold the workspace at {Root}", options.WorkspaceRoot);
            }
        }

        var bootRoot = SafeActivePath(app, options);
        StartupSignal.Progress("workspace.loading", $"Loading workspace {bootRoot}");
        var workspaceClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var workspace = app.Services.GetRequiredService<WorkspaceService>();
            StartupSignal.Progress(
                "workspace.loaded",
                $"Loaded {workspace.Current.Files.Count} workspace file(s) in {workspaceClock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            // Never fatal. A workspace that can't be read is a workspace problem — the UI can
            // still come up and let the user switch to another one, which is impossible if the
            // process dies here and the shell only sees "sidecar exited".
            app.Logger.LogError(ex, "Failed to load the workspace at {Root}", bootRoot);
            StartupSignal.Error("workspace.loading", $"Could not load the workspace at {bootRoot}: {ex.Message}");
        }

        // Studio's API is a control plane: it returns plaintext Key Vault / 1Password values and
        // OAuth tokens, reads and writes arbitrary workspace files, runs git, and spawns processes.
        // Binding to loopback keeps other machines out, but it is NOT a boundary against the
        // developer's own browser: Kestrel answers whatever Host header arrives, so a page on
        // attacker.example whose DNS flips to 127.0.0.1 becomes same-origin with this port and can
        // read every response. Pinning the acceptable Host values is what closes that; the origin
        // guard behind it is defence in depth. Same two layers as Tap.Server's inspector UI port.
        var (allowedHosts, allowAnyHost) = BuildHostAllowlist(options);

        if (!TryParseLoopback(options.Host, out _))
        {
            app.Logger.LogWarning(
                "Studio is binding to non-loopback host '{Host}'. Studio is a developer tool that can " +
                "execute arbitrary requests, read/write workspace files, and return secrets. Only do this " +
                "on a trusted network. {HostGuard}",
                options.Host,
                allowAnyHost
                    ? "Because that is a wildcard bind with no Studio:AllowedHosts set, the API accepts any " +
                      "Host header and cannot be defended against DNS rebinding — set Studio:AllowedHosts to " +
                      "the hostname(s) you actually browse it on."
                    : $"Accepted Host headers: {string.Join(", ", allowedHosts.Order())}.");
        }

        app.Use(HostAllowlist(allowedHosts, allowAnyHost));
        app.Use(RejectCrossOriginRequests);

        // Liveness for Aspire's WithHttpHealthCheck and WaitFor. Deliberately cheap — Kestrel is
        // answering and the workspace object exists — because a health check that walks the
        // workspace would make an AppHost's startup gate on a filesystem scan. A workspace that
        // loaded *with errors* is still healthy: the whole point of degrading to
        // "loaded with errors" is that Studio stays usable so you can fix them.
        app.MapGet("/health", (WorkspaceService svc) => Results.Ok(new
        {
            status = "healthy",
            mode = svc.Mode.ToString().ToLowerInvariant(),
            workspace = svc.RootDirectory,
            files = svc.Current.Files.Count,
        })).ExcludeFromDescription();

        WorkspaceEndpoints.Map(app);
        RequestEndpoints.Map(app);
        CatalogEndpoints.Map(app);
        CollectionEndpoints.Map(app);
        OpenApiEndpoints.Map(app);
        TagEndpoints.Map(app);
        StreamEndpoints.Map(app);
        HttpFileEndpoints.Map(app);
        ExecuteEndpoint.Map(app);
        ExecuteStreamEndpoint.Map(app);
        AssertEndpoints.Map(app);
        TestingEndpoints.Map(app);
        FileEndpoints.Map(app);
        FilesystemEndpoints.Map(app);
        GitEndpoints.Map(app);
        GraphQLSchemaEndpoint.Map(app);
        AuthFlowEndpoints.Map(app);
        VariableEndpoints.Map(app);
        ProviderEndpoints.Map(app);
        SystemEndpoints.Map(app);
        AzureEndpoints.Map(app);
        OnePasswordEndpoints.Map(app);
        BrowserEndpoints.Map(app);
        AiEndpoints.Map(app);
        app.MapMcp("/mcp");

        // Serve the bundled SPA. Tap.Studio.csproj's BuildStudioUi target copies
        // src/ui-studio/dist/** into wwwroot at build time. In Aspire dev where Vite
        // hosts the UI separately, wwwroot may be empty — the API still works.
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallback(ctx =>
        {
            // Anything not under /api/* gets index.html so the React router can
            // handle deep links. /api/* paths that don't match an endpoint fall
            // through to the framework's default 404 instead of returning HTML.
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            var indexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
            if (!File.Exists(indexPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.SendFileAsync(indexPath);
        });

        // Sidecar handshake. When wrapped by a desktop shell (Tauri) we let Kestrel
        // pick a free port (Studio:Port=0) and tell the shell what we got via a
        // single JSON line on stdout. See StartupSignal for the full protocol.
        if (StartupSignal.Enabled)
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var addresses = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
                var url = addresses.FirstOrDefault();
                if (url is null)
                {
                    // The shell is waiting for a URL it is never going to get; without
                    // this it waits out the whole startup timeout for no reason.
                    StartupSignal.Error("server.bind", "Kestrel started but reported no listening address.");
                    return;
                }
                StartupSignal.Ready(url);
            });
        }

        StartupSignal.Progress("server.starting", $"Binding to {options.Host}:{options.Port}");
        return app;
    }

    /// <summary>
    /// The folder the boot load is about to read. It comes from the known-workspace list
    /// (a previous session may have activated something other than <see cref="StudioOptions.WorkspaceRoot"/>),
    /// falling back to the configured root when that list itself can't be read — this runs
    /// purely to label a progress message, so it must not be able to fail the boot.
    /// </summary>
    private static string SafeActivePath(WebApplication app, StudioOptions options)
    {
        // Pinned hosts ignore the known-workspace list entirely, so reading it here would label
        // the progress message with a folder we are not about to open.
        if (options.IsWorkspacePinned) return options.WorkspaceRoot;

        try
        {
            return app.Services.GetRequiredService<KnownWorkspaceStore>().ActivePath;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Could not read the known-workspace list; assuming {Root}", options.WorkspaceRoot);
            return options.WorkspaceRoot;
        }
    }

    private static readonly string[] LoopbackHostNames = ["localhost", "127.0.0.1", "::1"];

    /// <summary>
    /// The Host values the API answers to. Wildcard binds ("0.0.0.0", "*", "+", "::") have no
    /// single correct hostname, so unless the operator names one via <c>Studio:AllowedHosts</c>
    /// we keep accepting any Host (container/LAN hosting still works) and warn at startup
    /// instead of silently breaking it.
    /// </summary>
    private static (HashSet<string> Hosts, bool AllowAny) BuildHostAllowlist(StudioOptions options)
    {
        var hosts = new HashSet<string>(LoopbackHostNames, StringComparer.OrdinalIgnoreCase);
        foreach (var extra in options.AllowedHosts)
        {
            hosts.Add(NormalizeHost(extra));
        }

        var isWildcard = options.Host is "0.0.0.0" or "*" or "+" or "::" or "[::]";
        if (!isWildcard)
        {
            hosts.Add(NormalizeHost(options.Host));
        }

        return (hosts, isWildcard && options.AllowedHosts.Length == 0);
    }

    /// <summary>Strips the brackets Kestrel keeps around an IPv6 literal so "[::1]" matches "::1".</summary>
    private static string NormalizeHost(string host) => host.Trim().Trim('[', ']');

    private static Func<HttpContext, RequestDelegate, Task> HostAllowlist(HashSet<string> allowed, bool allowAny) =>
        async (ctx, next) =>
        {
            if (allowAny || allowed.Contains(NormalizeHost(ctx.Request.Host.Host)))
            {
                await next(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsync(
                "Tap Studio: this Host is not allowed. Browse it on localhost, " +
                "or list the hostname in Studio:AllowedHosts.");
        };

    /// <summary>
    /// The one path that legitimately receives a cross-site top-level navigation: the identity
    /// provider redirects the user's browser here to finish an auth-code flow, so it arrives with
    /// <c>Sec-Fetch-Site: cross-site</c> by definition. It is a GET that renders an HTML page and
    /// hands nothing back to the initiator, so exempting it costs nothing.
    /// </summary>
    private const string OAuthCallbackPath = "/api/auth/callback";

    /// <summary>
    /// Keeps foreign origins off the control plane. Two layers:
    /// <list type="bullet">
    /// <item><c>Sec-Fetch-Site</c> — sent by every current browser and not forgeable from script,
    /// so it covers GET as well. That matters because the endpoints worth stealing
    /// (<c>/api/variable-providers/{name}/variables/{key}</c>, <c>/api/auth/flows/{id}</c>) are
    /// reads; a safe-method exemption would leave them wide open. Top-level document navigations
    /// stay allowed — following a link to the SPA, or landing on the OAuth callback, is
    /// legitimate and the response is not script-readable.</item>
    /// <item><c>Origin</c>/<c>Referer</c> — applied to unsafe methods, which is what closes the
    /// bodyless CORS "simple requests" (<c>POST /api/git/fetch</c>) and the antiforgery-disabled
    /// multipart upload. It compares against <c>ctx.Request.Host</c>, which is only trustworthy
    /// because <see cref="HostAllowlist"/> has already pinned that value.</item>
    /// </list>
    /// Non-browser clients (curl, the CLI, the Tauri shell's own HTTP calls) send neither header
    /// and are unaffected.
    /// </summary>
    private static async Task RejectCrossOriginRequests(HttpContext ctx, RequestDelegate next)
    {
        if (string.Equals(ctx.Request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase)
            && !IsTopLevelDocumentNavigation(ctx.Request))
        {
            await RejectCrossOrigin(ctx);
            return;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) ||
            HttpMethods.IsHead(ctx.Request.Method) ||
            HttpMethods.IsOptions(ctx.Request.Method))
        {
            await next(ctx);
            return;
        }

        var origin = ctx.Request.Headers.Origin.ToString();
        var referer = ctx.Request.Headers.Referer.ToString();
        if ((string.IsNullOrEmpty(origin) || IsSameOrigin(origin, ctx.Request.Host)) &&
            (string.IsNullOrEmpty(referer) || IsSameOrigin(referer, ctx.Request.Host)))
        {
            await next(ctx);
            return;
        }

        await RejectCrossOrigin(ctx);
    }

    private static bool IsTopLevelDocumentNavigation(HttpRequest req) =>
        (!req.Path.StartsWithSegments("/api") || req.Path.StartsWithSegments(OAuthCallbackPath)) &&
        string.Equals(req.Headers["Sec-Fetch-Mode"].ToString(), "navigate", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(req.Headers["Sec-Fetch-Dest"].ToString(), "document", StringComparison.OrdinalIgnoreCase);

    private static async Task RejectCrossOrigin(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync("Cross-origin control-plane requests are not allowed.");
    }

    /// <summary>
    /// Same-origin here means same hostname; the port is deliberately ignored. The Vite dev server
    /// proxies <c>/api</c> from :5297 to Studio on :5298, so the browser's Origin carries the dev
    /// port while the request Host carries Studio's — an exact port match would break every
    /// mutation in the dev loop. Both ends are still pinned to a hostname
    /// <see cref="HostAllowlist"/> accepts, which is what keeps a rebound attacker origin out.
    /// </summary>
    private static bool IsSameOrigin(string value, HostString host) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true (with the matching <see cref="IPAddress"/>) when <paramref name="host"/>
    /// names a loopback target — <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>, or any other
    /// address inside <c>127.0.0.0/8</c>. Anything else is treated as exposing the API beyond
    /// the local machine.
    /// </summary>
    internal static bool TryParseLoopback(string? host, out IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            address = IPAddress.Loopback;
            return true;
        }
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
            return true;
        }
        if (IPAddress.TryParse(host, out var parsed) && IPAddress.IsLoopback(parsed))
        {
            address = parsed;
            return true;
        }
        address = IPAddress.None;
        return false;
    }
}
