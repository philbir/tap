using Aspire.Hosting.Tunnels;
using Tap.Core.Cloudflare;

namespace Aspire.Hosting.Tailscale;

/// <summary>
/// One Tailscale Funnel binds one tailnet node's public URL
/// (https://&lt;machine&gt;.&lt;tailnet&gt;.ts.net) to one upstream service.
/// Funnel allows only ports 443, 8443, and 10000.
/// To expose multiple upstreams, register multiple <see cref="TailscaleFunnelResource"/>s
/// (one per tailnet node) — there is no per-hostname multiplexing on a single node.
/// </summary>
public sealed class TailscaleFunnelResource(string name, string command, string workingDirectory)
    : TapTunnelResource(name, command, workingDirectory)
{
    public override TunnelRoutingModel RoutingModel => TunnelRoutingModel.SinglePublicEndpoint;

    public override string ProviderId => "tailscale";

    /// <summary>
    /// True when the AppHost spawns its own ephemeral userspace tailscaled (with its own
    /// state dir + auth key) for this resource. False = use the system tailscaled daemon.
    /// </summary>
    public bool UseEphemeralDaemon { get; internal set; }

    /// <summary>Auth key used in ephemeral mode (`tailscale up --authkey=...`).</summary>
    public string? AuthKey { get; internal set; }

    /// <summary>Optional non-default login server (Headscale, etc.).</summary>
    public string? LoginServer { get; internal set; }

    /// <summary>Public Funnel port. Tailscale only allows 443 (default), 8443, 10000.</summary>
    public int FunnelPort { get; internal set; } = 443;

    /// <summary>Resolved &lt;machine&gt;.&lt;tailnet&gt;.ts.net hostname, populated by the lifecycle hook.</summary>
    public string? MagicDnsName { get; internal set; }

    /// <summary>Path of the per-resource state dir created in ephemeral mode (under <see cref="Path.GetTempPath"/>).</summary>
    public string? StateDir { get; internal set; }

    /// <summary>Path to the LocalAPI socket. Either system default or the daemon resource's per-state-dir socket.</summary>
    public string? SocketPath { get; internal set; }

    /// <summary>The companion daemon resource, present only when <see cref="UseEphemeralDaemon"/> is true.</summary>
    internal TailscaledDaemonResource? Daemon { get; set; }

    /// <summary>
    /// Where the ephemeral daemon runs: a host process (default) or a `tailscale/tailscale`
    /// Docker container. Docker mode lets you use ephemeral mode without a host `tailscaled`
    /// install (e.g. on macOS where the GUI client doesn't expose one).
    /// </summary>
    public TailscaleHostMode HostMode { get; internal set; } = TailscaleHostMode.Process;

    /// <summary>Container name for Docker mode. Set by the lifecycle hook so cleanup can `docker rm -f` it.</summary>
    internal string? DockerContainerName { get; set; }

    /// <summary>Image to run in Docker mode.</summary>
    public string DockerImage { get; internal set; } = "tailscale/tailscale:latest";

    /// <summary>
    /// When <c>true</c>, the tunnel uses <c>tailscale funnel</c> — exposed to the public internet.
    /// When <c>false</c> (default) it uses <c>tailscale serve</c>, reachable only from your tailnet.
    /// Public exposure attracts opportunistic scanning the moment a hostname resolves; only enable
    /// it when you actually need internet-facing access.
    /// </summary>
    public bool PublicExpose { get; internal set; }
}
