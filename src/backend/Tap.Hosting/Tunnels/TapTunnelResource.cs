using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Tunnels;

/// <summary>
/// Routing model exposed by a tunnel provider. Determines whether a single
/// tunnel can fan out to many upstreams (Cloudflare) or is bound 1:1 to a
/// single upstream/public URL (Tailscale Funnel default).
/// </summary>
public enum TunnelRoutingModel
{
    /// <summary>One tunnel, many ingress entries dispatched by Host header. (Cloudflare named tunnels.)</summary>
    HostnameMultiplexed,
    /// <summary>One tunnel = one upstream and one fixed public URL. (Cloudflare quick tunnel, Tailscale Funnel.)</summary>
    SinglePublicEndpoint,
}

/// <summary>
/// Provider-agnostic base for any tunnel resource. Both Cloudflared and
/// Tailscale Funnel resources inherit this so consumer-facing code (TapHandle,
/// WithTap dispatch, inspector env propagation) can treat them uniformly.
/// </summary>
public abstract class TapTunnelResource(string name, string command, string workingDirectory)
    : ExecutableResource(name, command, workingDirectory)
{
    public abstract TunnelRoutingModel RoutingModel { get; }

    /// <summary>Provider name surfaced to Tap.Server via the Inspector__Provider env var.</summary>
    public abstract string ProviderId { get; }

    /// <summary>Generic ingress entries. Providers may also keep their own typed lists internally.</summary>
    public List<TapTunnelIngress> IngressEntries { get; } = [];

    public int? InspectorProxyPort { get; internal set; }

    public int? InspectorUiPort { get; internal set; }

    internal TapHandle? AttachedTap { get; set; }
}
