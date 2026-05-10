using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Tunnels;

/// <summary>
/// Provider-agnostic ingress entry: which upstream is exposed through a tunnel,
/// optionally on which endpoint, optionally at which public hostname.
/// Cloudflare populates Hostname at registration (or in the lifecycle hook for
/// dynamic mode); Tailscale Funnel populates it from the resolved MagicDNS name.
/// </summary>
public sealed class TapTunnelIngress(IResourceWithEndpoints target, string? endpointName = null, string? hostname = null)
{
    public IResourceWithEndpoints Target { get; } = target;
    public string? EndpointName { get; } = endpointName;
    public string? Hostname { get; internal set; } = hostname;
    public string? PublicUrl { get; internal set; }
}
