using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Tailscale;
using Aspire.Hosting.Tunnels;
using Microsoft.Extensions.DependencyInjection;
using Tap.Core.Cloudflare;

namespace Aspire.Hosting;

public static class TailscaleExtensions
{
    /// <summary>Tailscale Funnel only allows these public ports.</summary>
    public static readonly IReadOnlySet<int> AllowedFunnelPorts = new HashSet<int> { 443, 8443, 10000 };

    /// <summary>
    /// Add a Tailscale Funnel tunnel resource. By default it uses the system <c>tailscaled</c>
    /// daemon (must already be installed and logged in). Switch to a per-AppHost-session
    /// userspace daemon with <see cref="WithEphemeralDaemon"/>.
    /// </summary>
    /// <param name="hostMode">
    /// <see cref="TailscaleHostMode.Process"/> spawns a host <c>tailscaled</c> binary (requires
    /// the binary on PATH); <see cref="TailscaleHostMode.Docker"/> runs <c>tailscale/tailscale</c>
    /// in a container instead. Docker mode is useful when the host has only the macOS GUI
    /// client (no spawnable <c>tailscaled</c>) or to keep the daemon isolated.
    /// </param>
    public static IResourceBuilder<TailscaleFunnelResource> AddTailscaleFunnel(
        this IDistributedApplicationBuilder builder,
        string name = "tailscale-funnel",
        TailscaleHostMode hostMode = TailscaleHostMode.Process,
        string dockerImage = "tailscale/tailscale:latest",
        bool publicExpose = false)
    {
#pragma warning disable CS0618 // Eventing-based replacement is not yet wired; lifecycle hook works fine today.
        builder.Services.TryAddLifecycleHook<TailscaleLifecycleHook>();
#pragma warning restore CS0618

        // Command is the script interpreter; the actual bootstrapper script path is
        // generated and bound by the lifecycle hook (it needs the resolved upstream URL).
        var (command, _) = TailscaleLifecycleHook.ScriptInterpreter();
        var resource = new TailscaleFunnelResource(name, command, builder.Environment.ContentRootPath)
        {
            HostMode = hostMode,
            DockerImage = dockerImage,
            PublicExpose = publicExpose,
        };

        return builder.AddResource(resource).ExcludeFromManifest();
    }


    /// <summary>Use the host's system <c>tailscaled</c> (the default). The node must already be authenticated.</summary>
    public static IResourceBuilder<TailscaleFunnelResource> WithSystemDaemon(
        this IResourceBuilder<TailscaleFunnelResource> builder)
    {
        builder.Resource.UseEphemeralDaemon = false;
        builder.Resource.AuthKey = null;
        return builder;
    }

    /// <summary>
    /// Spawn a per-session userspace <c>tailscaled</c> for this resource, joined to the
    /// tailnet via <paramref name="authKey"/>. Each ephemeral resource consumes one
    /// auth-key use per <c>aspire run</c>; pass a reusable key (or generate one per run).
    /// Ephemeral nodes disappear from the tailnet on AppHost shutdown.
    /// </summary>
    public static IResourceBuilder<TailscaleFunnelResource> WithEphemeralDaemon(
        this IResourceBuilder<TailscaleFunnelResource> builder,
        string authKey,
        string? loginServer = null)
    {
        if (string.IsNullOrWhiteSpace(authKey))
        {
            throw new ArgumentException(
                "Tailscale auth key is empty. Set Tailscale:AuthKey via user-secrets or appsettings.",
                nameof(authKey));
        }

        builder.Resource.UseEphemeralDaemon = true;
        builder.Resource.AuthKey = authKey;
        builder.Resource.LoginServer = loginServer;

        // Register the daemon as a sibling Aspire resource so it shows up in the dashboard
        // with its own logs and shutdown ordering. Command depends on HostMode (set by
        // AddTailscaleFunnel — make sure to pick host mode there if you want Docker).
        var daemonCommand = builder.Resource.HostMode == TailscaleHostMode.Docker
            ? "docker"
            : "tailscaled";
        var daemonName = $"{builder.Resource.Name}-tailscaled";
        var daemon = builder.ApplicationBuilder
            .AddResource(new TailscaledDaemonResource(daemonName, daemonCommand, builder.ApplicationBuilder.Environment.ContentRootPath))
            .ExcludeFromManifest()
            .WithParentRelationship(builder.Resource);
        builder.WaitFor(daemon);
        builder.Resource.Daemon = daemon.Resource;
        return builder;
    }

    /// <summary>Public Funnel port. Tailscale only accepts 443 (default), 8443, or 10000.</summary>
    public static IResourceBuilder<TailscaleFunnelResource> WithFunnelPort(
        this IResourceBuilder<TailscaleFunnelResource> builder,
        int port)
    {
        if (!AllowedFunnelPorts.Contains(port))
        {
            throw new ArgumentException(
                $"Tailscale Funnel only supports ports {string.Join(", ", AllowedFunnelPorts)}; got {port}.",
                nameof(port));
        }

        builder.Resource.FunnelPort = port;
        return builder;
    }

    /// <summary>Attach a Tailscale Funnel tunnel to this tap. Tailscale Funnel = one upstream per node.</summary>
    public static TapHandle WithTunnel(
        this TapHandle tap,
        IResourceBuilder<TailscaleFunnelResource> tunnel)
    {
        AttachTunnelToTap(tunnel, tap);
        return tap;
    }

    /// <summary>
    /// Register a Tailscale tunnel that is reachable only from your tailnet (uses
    /// <c>tailscale serve</c>). This is the safe default — your upstream is not exposed
    /// to the public internet, so opportunistic scanners can't find it.
    /// Use <see cref="WithTailscaleFunnel(TapHandle,string?,Action{IResourceBuilder{TailscaleFunnelResource}}?,TailscaleHostMode,string)"/>
    /// when you actually need a public URL.
    /// </summary>
    public static TapHandle WithTailscaleServe(
        this TapHandle tap,
        string? name = null,
        Action<IResourceBuilder<TailscaleFunnelResource>>? configure = null,
        TailscaleHostMode hostMode = TailscaleHostMode.Process,
        string dockerImage = "tailscale/tailscale:latest")
        => AttachTailscale(tap, name, configure, hostMode, dockerImage, publicExpose: false);

    /// <summary>
    /// Register a Tailscale Funnel — <b>publicly exposed on the internet</b>. Useful for
    /// webhook receivers and partner demos, but opportunistic scanners typically find new
    /// funnel hostnames within minutes. Always pair with <c>.WithHeaderAuth()</c> /
    /// <c>.WithIpAllowList()</c> / <c>.WithOidcAuth()</c> on the tap.
    /// For tailnet-only access, use <see cref="WithTailscaleServe"/> instead.
    /// </summary>
    public static TapHandle WithTailscaleFunnel(
        this TapHandle tap,
        string? name = null,
        Action<IResourceBuilder<TailscaleFunnelResource>>? configure = null,
        TailscaleHostMode hostMode = TailscaleHostMode.Process,
        string dockerImage = "tailscale/tailscale:latest")
        => AttachTailscale(tap, name, configure, hostMode, dockerImage, publicExpose: true);

    private static TapHandle AttachTailscale(
        TapHandle tap,
        string? name,
        Action<IResourceBuilder<TailscaleFunnelResource>>? configure,
        TailscaleHostMode hostMode,
        string dockerImage,
        bool publicExpose)
    {
        var tunnel = tap.ApplicationBuilder.AddTailscaleFunnel(
            name ?? (publicExpose ? $"{tap.Resource.Name}-funnel" : $"{tap.Resource.Name}-serve"),
            hostMode: hostMode,
            dockerImage: dockerImage,
            publicExpose: publicExpose);
        configure?.Invoke(tunnel);
        AttachTunnelToTap(tunnel, tap);
        tunnel.WithParentRelationship(tap.Resource);
        return tap;
    }

    /// <summary>
    /// Route this resource's traffic through a Tailscale Funnel. Funnel exposes ONE upstream
    /// per tailnet node; calling this twice on the same tunnel resource throws.
    /// </summary>
    public static IResourceBuilder<T> WithTailscaleFunnel<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<TailscaleFunnelResource> tunnel,
        string? endpointName = null)
        where T : IResourceWithEndpoints
    {
        if (tunnel.Resource.IngressEntries.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tailscale Funnel resource '{tunnel.Resource.Name}' already has an upstream attached. "
                + "Funnel exposes one upstream per tailnet node — register a second AddTailscaleFunnel(...) "
                + "for additional upstreams.");
        }

        var entry = new TapTunnelIngress(builder.Resource, endpointName);
        tunnel.Resource.IngressEntries.Add(entry);
        builder.WithAnnotation(new TapTunnelAnnotation(tunnel.Resource, hostname: null));

        if (tunnel.Resource.AttachedTap is { } tap)
        {
            tap.Annotation.Entries.Add(new TapIngressEntry(null, builder.Resource, endpointName)
            {
                TunnelMode = tunnel.Resource.UseEphemeralDaemon ? "tailscale-ephemeral" : "tailscale-system",
                TunnelName = tunnel.Resource.Name,
                PublicUrl = null,
            });
        }

        return builder;
    }

    private static void AttachTunnelToTap(
        IResourceBuilder<TailscaleFunnelResource> tunnel,
        TapHandle tap)
    {
        tunnel.Resource.InspectorProxyPort = tap.Annotation.ProxyPort;
        tunnel.Resource.InspectorUiPort = tap.Annotation.UiPort;
        tunnel.Resource.AttachedTap = tap;
        tap.AttachedTunnel = tunnel.Resource;

        // Mirror any pre-attached ingress into the tap.
        foreach (var entry in tunnel.Resource.IngressEntries)
        {
            tap.Annotation.Entries.Add(new TapIngressEntry(entry.Hostname, entry.Target, entry.EndpointName)
            {
                TunnelMode = TailscaleTunnelModeOf(tunnel.Resource),
                TunnelName = tunnel.Resource.Name,
                PublicUrl = entry.PublicUrl,
                PublicExpose = tunnel.Resource.PublicExpose,
            });
        }

        var tunnelResource = tunnel.Resource;
        tap.WithUrlForEndpoint("proxy", url =>
        {
            if (!string.IsNullOrEmpty(tunnelResource.MagicDnsName))
            {
                url.Url = BuildPublicUrl(tunnelResource);
                url.DisplayText = "Public URL";
            }
        });

        tap.WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables["Inspector__Provider"] = tunnelResource.ProviderId;
            ctx.EnvironmentVariables["Inspector__Tunnel__Mode"] = TailscaleTunnelModeOf(tunnelResource);
            ctx.EnvironmentVariables["Inspector__Tunnel__Name"] = tunnelResource.Name;
            ctx.EnvironmentVariables["Inspector__Tunnel__ResourceName"] = tunnelResource.Name;
            ctx.EnvironmentVariables["Inspector__Tunnel__PublicExpose"] = tunnelResource.PublicExpose ? "true" : "false";

            if (!string.IsNullOrEmpty(tunnelResource.MagicDnsName))
            {
                ctx.EnvironmentVariables["Inspector__Tunnel__PublicUrl"] = BuildPublicUrl(tunnelResource);
                ctx.EnvironmentVariables["Inspector__Tunnel__Hostname"] = tunnelResource.MagicDnsName!;
            }
            ctx.EnvironmentVariables["Inspector__Tunnel__Port"] = tunnelResource.FunnelPort.ToString();

            // Ephemeral mode: pass the per-resource socket path so Tap.Server can query
            // the right tailscaled instance (system mode leaves this null → CLI default).
            // Skipped when the tap itself is a container (AddTapContainer): the host's socket
            // path isn't reachable from inside the tap container, and the docker socket bind-mount
            // would be more trouble than it's worth — the daemon panel will show "unavailable"
            // for this configuration, which is honest. Likewise for Docker mode (no socket).
            var skipSocket = tap.Resource is ContainerResource || tunnelResource.HostMode == TailscaleHostMode.Docker;
            if (!skipSocket && !string.IsNullOrEmpty(tunnelResource.SocketPath))
            {
                ctx.EnvironmentVariables["Inspector__Tunnel__SocketPath"] = tunnelResource.SocketPath!;
            }
        });
    }

    internal static string BuildPublicUrl(TailscaleFunnelResource tunnel) =>
        tunnel.FunnelPort == 443
            ? $"https://{tunnel.MagicDnsName}"
            : $"https://{tunnel.MagicDnsName}:{tunnel.FunnelPort}";

    /// <summary>
    /// Inspector-side tunnelMode label. We keep system/ephemeral as the primary axis and
    /// surface public/tailnet as a separate `PublicExpose` flag so existing UI string-
    /// switches keep working.
    /// </summary>
    internal static string TailscaleTunnelModeOf(TailscaleFunnelResource tunnel) =>
        tunnel.UseEphemeralDaemon ? "tailscale-ephemeral" : "tailscale-system";
}
