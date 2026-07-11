using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Json;
using Tap.Studio.Auth;
using Tap.Studio.Contracts;
using Tap.Studio.Endpoints;
using Tap.Studio.Variables;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;

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
        builder.Services.AddSingleton<SystemSettingsStore>();
        builder.Services.AddSingleton<Tap.Studio.Ai.AiProviderFactory>();
        builder.Services.AddSingleton<KnownWorkspaceStore>();
        builder.Services.AddSingleton<WorkspaceService>();
        builder.Services.AddSingleton<GitService>();
        builder.Services.AddHttpContextAccessor();

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
        builder.Services.AddSingleton<AuthTokenStore>();

        // Variable provider factories. Each registered factory adds support for one
        // `type:` in workspace + system provider config. Add a new factory here to
        // make a new provider type available everywhere.
        builder.Services.AddSingleton<IVariableProviderFactory, EnvVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, FileVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, AzureKeyVaultVariableProviderFactory>();
        builder.Services.AddSingleton<IVariableProviderFactory, SystemVariableProviderFactory>();
        builder.Services.AddSingleton<ProviderRegistryBuilder>();

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, StudioJson.Default);
        });

        var app = builder.Build();
        _ = app.Services.GetRequiredService<WorkspaceService>();

        if (!TryParseLoopback(options.Host, out _))
        {
            app.Logger.LogWarning(
                "Studio is binding to non-loopback host '{Host}'. Studio is a developer tool that can " +
                "execute arbitrary requests, read/write workspace files, and return secrets. Only do this " +
                "on a trusted network.",
                options.Host);
        }

        WorkspaceEndpoints.Map(app);
        RequestEndpoints.Map(app);
        CatalogEndpoints.Map(app);
        CollectionEndpoints.Map(app);
        TagEndpoints.Map(app);
        StreamEndpoints.Map(app);
        ExecuteEndpoint.Map(app);
        ExecuteStreamEndpoint.Map(app);
        FileEndpoints.Map(app);
        FilesystemEndpoints.Map(app);
        GitEndpoints.Map(app);
        GraphQLSchemaEndpoint.Map(app);
        AuthFlowEndpoints.Map(app);
        VariableEndpoints.Map(app);
        ProviderEndpoints.Map(app);
        SystemEndpoints.Map(app);
        AiEndpoints.Map(app);

        // Serve the bundled SPA. Tap.Studio.csproj's BuildStudioUi target copies
        // ui-studio/dist/** into wwwroot at build time. In Aspire dev where Vite
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
        // single JSON line on stdout. Gated by TAP_STUDIO_EMIT_READY=1 so normal
        // Aspire-dashboard runs don't leak this into the log stream.
        if (string.Equals(Environment.GetEnvironmentVariable("TAP_STUDIO_EMIT_READY"), "1", StringComparison.Ordinal))
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var addresses = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
                var url = addresses.FirstOrDefault();
                if (url is null) return;
                var line = JsonSerializer.Serialize(new
                {
                    @event = "studio.ready",
                    url,
                    pid = Environment.ProcessId,
                });
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            });
        }

        return app;
    }

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
