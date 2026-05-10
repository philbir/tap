using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Tunnels;

/// <summary>
/// Provider-agnostic marker that a resource has been attached to a tunnel.
/// Lives alongside any provider-specific annotation (e.g. CloudflareTunnelAnnotation)
/// so generic code can read tunnel state without knowing which provider supplied it.
/// </summary>
public sealed class TapTunnelAnnotation(TapTunnelResource tunnel, string? hostname)
    : IResourceAnnotation
{
    public TapTunnelResource Tunnel { get; } = tunnel;

    public string? Hostname { get; internal set; } = hostname;

    public string? PublicUrl => string.IsNullOrEmpty(Hostname) ? null : $"https://{Hostname}";
}
