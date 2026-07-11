using Aspire.Hosting.Tunnels;
using Tap.Core.Cloudflared;

namespace Aspire.Hosting.Cloudflared;

public sealed class CloudflaredTunnelResource(string name, string command, string workingDirectory)
    : TapTunnelResource(name, command, workingDirectory)
{
    public override TunnelRoutingModel RoutingModel =>
        IsQuickTunnel ? TunnelRoutingModel.SinglePublicEndpoint : TunnelRoutingModel.HostnameMultiplexed;

    public override string ProviderId => "cloudflare";

    public string DockerImage { get; internal set; } = "cloudflare/cloudflared:latest";

    public string? Token { get; internal set; }

    public bool UseLocalIngress { get; internal set; }

    public string? TunnelId { get; internal set; }

    public string? CredentialsFilePath { get; internal set; }

    // Cloudflare API-managed tunnel: lifecycle hook will look up or create the tunnel
    // and write credentials before cloudflared launches.
    internal string? ApiToken { get; set; }
    internal string? AccountId { get; set; }
    internal string? ApiTunnelName { get; set; }

    // Dynamic-host mode.
    internal string? DynamicZoneName { get; set; }
    internal string? DynamicZoneId { get; set; }
    internal string? DynamicHostnamePrefix { get; set; }
    internal string? DynamicHostnameSuffix { get; set; }

    internal bool ManageDns { get; set; }

    // Quick / TryCloudflare mode.
    internal bool IsQuickTunnel { get; set; }
    internal string? QuickLocalUrl { get; set; }
    public string? QuickPublicUrl { get; internal set; }

    /// <summary>Local cloudflared binary or Docker container.</summary>
    public CloudflaredHostMode HostMode { get; internal set; } = CloudflaredHostMode.Process;

    /// <summary>If true, the lifecycle hook will install cloudflared via the OS package manager when missing.</summary>
    public bool AutoInstall { get; internal set; }
}
