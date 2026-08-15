using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Tailscale;

/// <summary>
/// Userspace tailscaled spawned for a <see cref="TailscaleFunnelResource"/> in ephemeral mode.
/// Modeled as a first-class Aspire resource so its log stream, lifecycle (kill on shutdown),
/// and state-dir cleanup are all managed by the dashboard rather than as a leaking
/// side-process inside the lifecycle hook.
///
/// Process mode: command is <c>tailscaled</c>, args are <c>--tun=userspace-networking ...</c>.
/// Docker mode: command is <c>docker</c>, args are <c>run --rm --name ... tailscale/tailscale</c>.
/// </summary>
public sealed class TailscaledDaemonResource(string name, string command, string workingDirectory)
    : ExecutableResource(name, command, workingDirectory)
{
    public string? StateDir { get; internal set; }

    public string? SocketPath { get; internal set; }

    /// <summary>Container name when running under Docker; null otherwise.</summary>
    public string? DockerContainerName { get; internal set; }
}
