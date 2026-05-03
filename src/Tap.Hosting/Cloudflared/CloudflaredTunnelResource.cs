using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Cloudflared;

public sealed class CloudflaredTunnelResource(string name, string workingDirectory)
    : ExecutableResource(name, "cloudflared", workingDirectory)
{
    public string? Token { get; internal set; }

    public bool UseLocalIngress { get; internal set; }

    public string? TunnelId { get; internal set; }

    public string? CredentialsFilePath { get; internal set; }

    public List<CloudflaredIngressEntry> IngressEntries { get; } = [];

    public int? InspectorProxyPort { get; internal set; }

    public int? InspectorUiPort { get; internal set; }

    internal HttpInspectorHandle? AttachedInspector { get; set; }
}

public sealed record CloudflaredIngressEntry(
    string Hostname,
    IResourceWithEndpoints Target,
    string? EndpointName);
