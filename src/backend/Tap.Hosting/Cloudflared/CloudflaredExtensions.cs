using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Cloudflared;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Tunnels;
using Microsoft.Extensions.DependencyInjection;
using Tap.Core.Cloudflared;

namespace Aspire.Hosting;

public static class CloudflaredExtensions
{
    /// <summary>
    /// Add a cloudflared tunnel to the AppHost.
    /// </summary>
    /// <param name="hostMode">
    /// <see cref="CloudflaredHostMode.Process"/> spawns the local <c>cloudflared</c> binary
    /// (auto-installable via <see cref="WithAutoInstall"/>); <see cref="CloudflaredHostMode.Docker"/>
    /// runs <c>cloudflare/cloudflared:latest</c> in a Docker container.
    /// </param>
    public static IResourceBuilder<CloudflaredTunnelResource> AddCloudflaredTunnel(
        this IDistributedApplicationBuilder builder,
        string name = "cloudflared",
        CloudflaredHostMode hostMode = CloudflaredHostMode.Process,
        string dockerImage = "cloudflare/cloudflared:latest")
    {
#pragma warning disable CS0618 // Eventing-based replacement is not yet wired; lifecycle hook works fine today.
        builder.Services.TryAddLifecycleHook<CloudflaredLifecycleHook>();
#pragma warning restore CS0618

        var command = hostMode == CloudflaredHostMode.Docker ? "docker" : "cloudflared";
        var resource = new CloudflaredTunnelResource(name, command, builder.Environment.ContentRootPath)
        {
            HostMode = hostMode,
            DockerImage = dockerImage,
        };

        return builder.AddResource(resource)
            .ExcludeFromManifest()
            .WithArgs(async ctx =>
            {
                var inner = new List<string> { "tunnel", "--no-autoupdate" };

                if (resource.IsQuickTunnel)
                {
                    inner.Add("--url");
                    inner.Add(resource.QuickLocalUrl!);
                }
                else if (resource.UseLocalIngress)
                {
                    var configPath = await WriteConfigYamlAsync(resource, ctx.CancellationToken);
                    inner.Add("--config");
                    inner.Add(configPath);
                    inner.Add("run");
                    inner.Add(resource.TunnelId!);
                }
                else
                {
                    inner.Add("run");
                    inner.Add("--token");
                    inner.Add(resource.Token!);
                }

                if (resource.HostMode == CloudflaredHostMode.Docker)
                {
                    // docker run --rm --network host [--volume creds] [--volume config] <image> <inner...>
                    ctx.Args.Add("run");
                    ctx.Args.Add("--rm");
                    ctx.Args.Add("--network"); ctx.Args.Add("host");
                    if (!string.IsNullOrEmpty(resource.CredentialsFilePath))
                    {
                        ctx.Args.Add("-v"); ctx.Args.Add($"{resource.CredentialsFilePath}:{resource.CredentialsFilePath}:ro");
                    }
                    // Find the --config arg's value if present and mount it.
                    var idx = inner.IndexOf("--config");
                    if (idx >= 0 && idx + 1 < inner.Count)
                    {
                        ctx.Args.Add("-v"); ctx.Args.Add($"{inner[idx + 1]}:{inner[idx + 1]}:ro");
                    }
                    ctx.Args.Add(resource.DockerImage);
                }
                foreach (var a in inner) ctx.Args.Add(a);
            });
    }

    /// <summary>If cloudflared isn't on PATH at startup, install it via the host's package manager.</summary>
    public static IResourceBuilder<CloudflaredTunnelResource> WithAutoInstall(
        this IResourceBuilder<CloudflaredTunnelResource> builder)
    {
        builder.Resource.AutoInstall = true;
        return builder;
    }

    /// <summary>
    /// Run cloudflared in TryCloudflare / "quick tunnel" mode (<c>cloudflared tunnel --url ...</c>).
    /// No Cloudflare account, no credentials, no DNS records — Cloudflare assigns a random
    /// <c>*.trycloudflare.com</c> hostname when the tunnel comes up. The URL is parsed from
    /// cloudflared's stdout and exposed on this resource and in the Aspire dashboard.
    /// </summary>
    public static IResourceBuilder<CloudflaredTunnelResource> WithQuickTunnel(
        this IResourceBuilder<CloudflaredTunnelResource> builder,
        string localUrl)
    {
        if (string.IsNullOrWhiteSpace(localUrl)) throw new ArgumentException("localUrl required.", nameof(localUrl));
        builder.Resource.IsQuickTunnel = true;
        builder.Resource.QuickLocalUrl = localUrl;
        builder.Resource.UseLocalIngress = false;
        return builder;
    }

    /// <summary>
    /// Connect to a tunnel that already exists in the Cloudflare dashboard, using its
    /// connector token. The token is the secret value from <c>cloudflared tunnel run --token ...</c>;
    /// Tap will not create or reconfigure the tunnel — only run cloudflared against it.
    /// </summary>
    public static IResourceBuilder<CloudflaredTunnelResource> WithExistingTunnel(
        this IResourceBuilder<CloudflaredTunnelResource> builder,
        string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException(
                "Cloudflared tunnel token is empty. Set Cloudflare:TunnelToken via user-secrets or appsettings.",
                nameof(token));
        }

        builder.Resource.Token = token;
        builder.Resource.UseLocalIngress = false;
        return builder;
    }

    public static IResourceBuilder<CloudflaredTunnelResource> WithLocalIngress(
        this IResourceBuilder<CloudflaredTunnelResource> builder,
        string tunnelId,
        string credentialsFilePath)
    {
        builder.Resource.UseLocalIngress = true;
        builder.Resource.TunnelId = tunnelId;
        builder.Resource.CredentialsFilePath = credentialsFilePath;
        return builder;
    }

    /// <summary>
    /// Use the Cloudflare API to look up (or create) a named tunnel before cloudflared launches.
    /// The credentials file is written to the system temp dir and consumed by cloudflared in
    /// local-ingress mode. Requires an API token with Account:Cloudflare Tunnel:Edit, plus DNS:Edit
    /// on the target zone if any ingress entry is publicly exposed.
    /// </summary>
    public static IResourceBuilder<CloudflaredTunnelResource> WithApiManagedTunnel(
        this IResourceBuilder<CloudflaredTunnelResource> builder,
        string apiToken,
        string accountId,
        string? tunnelName = null)
    {
        if (string.IsNullOrWhiteSpace(apiToken)) throw new ArgumentException("API token required.", nameof(apiToken));
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("Account ID required.", nameof(accountId));

        builder.Resource.UseLocalIngress = true;
        builder.Resource.ApiToken = apiToken;
        builder.Resource.AccountId = accountId;
        builder.Resource.ApiTunnelName = tunnelName ?? $"tap-{builder.Resource.Name}";
        builder.Resource.ManageDns = true;
        return builder;
    }

    /// <summary>
    /// Mint hostnames per session under <paramref name="zoneName"/>. Requires <see cref="WithApiManagedTunnel"/>.
    /// Each ingress added with <c>.WithTap(tap)</c> (no hostname) gets a fresh
    /// <c>{prefix}{random}{suffix}.{zoneName}</c> CNAME pointing at the tunnel. Both <paramref name="prefix"/>
    /// and <paramref name="suffix"/> are taken literally — include any separator characters yourself
    /// (e.g. <c>prefix: "api-", suffix: "-tap"</c> on zone <c>p7e.dev</c> → <c>api-1a2b3c4d-tap.p7e.dev</c>).
    /// </summary>
    public static IResourceBuilder<CloudflaredTunnelResource> WithDynamicHostname(
        this IResourceBuilder<CloudflaredTunnelResource> builder,
        string zoneName,
        string? zoneId = null,
        string prefix = "svc-",
        string suffix = "")
    {
        if (string.IsNullOrWhiteSpace(zoneName)) throw new ArgumentException("Zone name required.", nameof(zoneName));
        if (builder.Resource.ApiToken is null)
        {
            throw new InvalidOperationException(
                "WithDynamicHostname() requires WithApiManagedTunnel() to be called first.");
        }

        builder.Resource.DynamicZoneName = zoneName;
        builder.Resource.DynamicZoneId = zoneId;
        builder.Resource.DynamicHostnamePrefix = prefix;
        builder.Resource.DynamicHostnameSuffix = suffix;
        builder.Resource.ManageDns = true;
        return builder;
    }

    /// <summary>
    /// Attach a Cloudflare tunnel to this tap. The tunnel is created as a child resource of
    /// the tap in the Aspire dashboard; configure it via <paramref name="configure"/> with
    /// <see cref="WithExistingTunnel"/>, <see cref="WithApiManagedTunnel"/>, etc.
    /// Traffic flows: internet → Cloudflare → cloudflared → tap proxy → upstream.
    /// </summary>
    public static TapHandle WithTunnel(
        this TapHandle tap,
        string name,
        Action<IResourceBuilder<CloudflaredTunnelResource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var tunnel = tap.ApplicationBuilder.AddCloudflaredTunnel(name);
        configure(tunnel);
        AttachTunnelToTap(tunnel, tap);
        tunnel.WithParentRelationship(tap.Resource);
        return tap;
    }

    /// <summary>
    /// Attach a TryCloudflare quick tunnel to this tap. No Cloudflare account or DNS setup
    /// required — Cloudflare assigns a random <c>*.trycloudflare.com</c> URL at startup
    /// and Tap surfaces it in the dashboard.
    /// </summary>
    public static TapHandle WithQuickTunnel(this TapHandle tap, string? name = null)
    {
        return tap.WithTunnel(
            name ?? $"{tap.Resource.Name}-tunnel",
            t => t.WithQuickTunnel($"http://localhost:{tap.Annotation.ProxyPort}"));
    }

    /// <summary>
    /// Attach an already-registered tunnel (from <see cref="AddCloudflaredTunnel"/>) to this tap.
    /// Use this when you need direct access to the tunnel resource builder.
    /// </summary>
    public static TapHandle WithTunnel(
        this TapHandle tap,
        IResourceBuilder<CloudflaredTunnelResource> tunnel)
    {
        AttachTunnelToTap(tunnel, tap);
        return tap;
    }

    private static void AttachTunnelToTap(
        IResourceBuilder<CloudflaredTunnelResource> tunnel,
        TapHandle tap)
    {
        tunnel.Resource.InspectorProxyPort = tap.Annotation.ProxyPort;
        tunnel.Resource.InspectorUiPort = tap.Annotation.UiPort;
        tunnel.Resource.AttachedTap = tap;
        tap.AttachedTunnel = tunnel.Resource;

        // Mirror any ingress entries that were attached to the tunnel before this call.
        foreach (var entry in tunnel.Resource.IngressEntries)
        {
            tap.Annotation.Entries.Add(new TapIngressEntry(entry.Hostname, entry.Target, entry.EndpointName));
        }

        // Re-target the tap's "proxy" URL chip in the Aspire dashboard to the public URL
        // once the tunnel comes up — that's what users actually want to click.
        var tunnelResource = tunnel.Resource;
        tap.WithUrlForEndpoint("proxy", url =>
        {
            var publicUrl = ResolvePublicUrl(tunnelResource);
            if (!string.IsNullOrEmpty(publicUrl))
            {
                url.Url = publicUrl;
                url.DisplayText = "Public URL";
            }
        });

        // Push per-tap tunnel context so the Inspector UI / API can show details,
        // resolve via Cloudflare API, and link to the dashboard.
        tap.WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables["Inspector__Provider"] = tunnelResource.ProviderId;
            ctx.EnvironmentVariables["Inspector__Tunnel__Mode"] = TunnelModeOf(tunnelResource);
            ctx.EnvironmentVariables["Inspector__Tunnel__Name"] = tunnelResource.ApiTunnelName ?? tunnelResource.Name;
            ctx.EnvironmentVariables["Inspector__Tunnel__ResourceName"] = tunnelResource.Name;
            // Cloudflare tunnels are always public.
            ctx.EnvironmentVariables["Inspector__Tunnel__PublicExpose"] = "true";

            var publicUrl = ResolvePublicUrl(tunnelResource);
            if (!string.IsNullOrEmpty(publicUrl))
            {
                ctx.EnvironmentVariables["Inspector__Tunnel__PublicUrl"] = publicUrl;
            }

            if (!string.IsNullOrEmpty(tunnelResource.AccountId))
            {
                ctx.EnvironmentVariables["Inspector__Tunnel__AccountId"] = tunnelResource.AccountId!;
            }
            if (!string.IsNullOrEmpty(tunnelResource.TunnelId))
            {
                ctx.EnvironmentVariables["Inspector__Tunnel__TunnelId"] = tunnelResource.TunnelId!;
            }
            if (!string.IsNullOrEmpty(tunnelResource.ApiToken))
            {
                ctx.EnvironmentVariables["Inspector__Tunnel__ApiToken"] = tunnelResource.ApiToken!;
            }

            // Existing-tunnel mode: the token is base64url JSON {a:account, t:tunnelId, s:secret}.
            // Parse it so we can still deep-link into the dashboard without an API token.
            if (!string.IsNullOrEmpty(tunnelResource.Token))
            {
                if (TryDecodeTokenIds(tunnelResource.Token!, out var accountId, out var tunnelId))
                {
                    ctx.EnvironmentVariables["Inspector__Tunnel__AccountId"] = accountId;
                    ctx.EnvironmentVariables["Inspector__Tunnel__TunnelId"] = tunnelId;
                }
            }
        });
    }

    private static bool TryDecodeTokenIds(string token, out string accountId, out string tunnelId)
    {
        accountId = "";
        tunnelId = "";
        try
        {
            // Cloudflared connector tokens are base64-encoded JSON.
            var padded = token.Length % 4 == 0 ? token : token + new string('=', 4 - token.Length % 4);
            var bytes = Convert.FromBase64String(padded);
            using var doc = System.Text.Json.JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("a", out var a) && doc.RootElement.TryGetProperty("t", out var t))
            {
                accountId = a.GetString() ?? "";
                tunnelId = t.GetString() ?? "";
                return !string.IsNullOrEmpty(accountId) && !string.IsNullOrEmpty(tunnelId);
            }
        }
        catch { }
        return false;
    }

    private static string? ResolvePublicUrl(CloudflaredTunnelResource tunnel)
    {
        if (tunnel.IsQuickTunnel)
        {
            return tunnel.QuickPublicUrl; // populated lazily by the lifecycle-hook log watcher
        }
        var first = tunnel.IngressEntries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Hostname));
        return first is null ? null : $"https://{first.Hostname}";
    }

    public static IResourceBuilder<T> WithCloudflareTunnel<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<CloudflaredTunnelResource> tunnel,
        string? hostname = null,
        string? endpointName = null)
        where T : IResourceWithEndpoints
    {
        if (string.IsNullOrEmpty(hostname)
            && tunnel.Resource.DynamicZoneName is null
            && !tunnel.Resource.IsQuickTunnel)
        {
            throw new InvalidOperationException(
                $"hostname is required for tunnel '{tunnel.Resource.Name}' (no dynamic-host zone configured). "
                + "Either pass a hostname or call .WithDynamicHostname(zone)/.WithQuickTunnel(...) on the tunnel.");
        }

        var entry = new TapTunnelIngress(builder.Resource, endpointName, hostname);
        tunnel.Resource.IngressEntries.Add(entry);
        builder.WithAnnotation(new CloudflareTunnelAnnotation(tunnel.Resource, hostname ?? string.Empty));
        builder.WithAnnotation(new TapTunnelAnnotation(tunnel.Resource, hostname));

        // If a tap is attached to this tunnel, mirror the ingress so it can proxy + capture
        // the same traffic. (For dynamic hosts, the tap entry gets its hostname later in
        // the lifecycle hook.)
        if (tunnel.Resource.AttachedTap is { } tap)
        {
            tap.Annotation.Entries.Add(new TapIngressEntry(hostname, builder.Resource, endpointName)
            {
                TunnelMode = TunnelModeOf(tunnel.Resource),
                TunnelName = tunnel.Resource.ApiTunnelName,
                PublicUrl = string.IsNullOrEmpty(hostname) ? null : $"https://{hostname}",
                PublicExpose = true, // Cloudflare tunnels are always public.
            });
        }

        return builder;
    }

    private static string TunnelModeOf(CloudflaredTunnelResource tunnel)
    {
        if (tunnel.IsQuickTunnel) return "quick";
        if (!tunnel.UseLocalIngress) return "existing";
        if (tunnel.DynamicZoneName is not null) return "dynamic";
        if (tunnel.ApiToken is not null) return "api-managed";
        return "local";
    }

    public static string GetCloudflareTunnelUrl<T>(this IResourceBuilder<T> builder)
        where T : IResource
    {
        if (!builder.Resource.TryGetAnnotationsOfType<CloudflareTunnelAnnotation>(out var annotations))
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no Cloudflare tunnel attached. Call .WithCloudflareTunnel(...) first.");
        }

        return annotations.First().PublicUrl;
    }

    private static async Task<string> WriteConfigYamlAsync(
        CloudflaredTunnelResource tunnel,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cloudflared-{tunnel.Name}-{Guid.NewGuid():N}.yml");
        var lines = new List<string>
        {
            $"tunnel: {tunnel.TunnelId}",
            $"credentials-file: {tunnel.CredentialsFilePath}",
            "ingress:",
        };

        foreach (var entry in tunnel.IngressEntries)
        {
            // If an inspector is attached, route ALL ingress through it so traffic is captured.
            // The inspector then resolves hostname -> real upstream via its own ingress config.
            var service = tunnel.InspectorProxyPort.HasValue
                ? $"http://localhost:{tunnel.InspectorProxyPort.Value}"
                : ResolveLocalUrl(entry);

            lines.Add($"  - hostname: {entry.Hostname}");
            lines.Add($"    service: {service}");
        }

        lines.Add("  - service: http_status:404");

        await File.WriteAllLinesAsync(path, lines, cancellationToken);
        return path;
    }

    private static string ResolveLocalUrl(TapTunnelIngress entry) =>
        TapExtensions.ResolveLocalUrl(new TapIngressEntry(entry.Hostname, entry.Target, entry.EndpointName));
}
