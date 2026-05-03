using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Cloudflared;

public sealed class CloudflareTunnelAnnotation(CloudflaredTunnelResource tunnel, string hostname)
    : IResourceAnnotation
{
    public CloudflaredTunnelResource Tunnel { get; } = tunnel;

    public string Hostname { get; } = hostname;

    public string PublicUrl => $"https://{Hostname}";
}
