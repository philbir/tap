using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Cloudflared;

#pragma warning disable CS0618 // Eventing-based replacement is not yet wired; lifecycle hook works fine today.
internal sealed class CloudflaredLifecycleHook(ILogger<CloudflaredLifecycleHook> logger)
    : IDistributedApplicationLifecycleHook
{
    public Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        var tunnels = appModel.Resources.OfType<CloudflaredTunnelResource>().ToList();
        if (tunnels.Count == 0)
        {
            return Task.CompletedTask;
        }

        VerifyCloudflaredAvailable();

        foreach (var tunnel in tunnels)
        {
            if (tunnel.UseLocalIngress)
            {
                if (string.IsNullOrWhiteSpace(tunnel.TunnelId) || string.IsNullOrWhiteSpace(tunnel.CredentialsFilePath))
                {
                    throw new InvalidOperationException(
                        $"Cloudflared tunnel '{tunnel.Name}' is in local-ingress mode but is missing TunnelId or CredentialsFilePath.");
                }
            }
            else if (string.IsNullOrWhiteSpace(tunnel.Token))
            {
                throw new InvalidOperationException(
                    $"Cloudflared tunnel '{tunnel.Name}' has no token. Call .WithToken(\"...\") or switch to .WithLocalIngress(...).");
            }

            logger.LogInformation(
                "Cloudflared tunnel '{Name}' configured with {Count} ingress entr{Suffix} ({Mode}).",
                tunnel.Name,
                tunnel.IngressEntries.Count,
                tunnel.IngressEntries.Count == 1 ? "y" : "ies",
                tunnel.UseLocalIngress ? "local-ingress" : "token");

            if (tunnel.InspectorProxyPort is { } inspectorPort)
            {
                logger.LogInformation(
                    "HTTP Inspector enabled. UI: http://localhost:{Ui}  |  Proxy port: {Proxy}",
                    tunnel.InspectorUiPort,
                    inspectorPort);

                if (!tunnel.UseLocalIngress)
                {
                    foreach (var entry in tunnel.IngressEntries)
                    {
                        logger.LogInformation(
                            "Token mode: point {Hostname} -> http://localhost:{Proxy} in the Cloudflare dashboard so traffic flows through the inspector.",
                            entry.Hostname,
                            inspectorPort);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }
#pragma warning restore CS0618

    private static void VerifyCloudflaredAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("cloudflared", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");

            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("cloudflared --version timed out");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException { Message: var m } || !m.Contains("cloudflared"))
        {
            throw new InvalidOperationException(
                "cloudflared not found on PATH. Install with: brew install cloudflared (macOS) "
                + "or winget install --id Cloudflare.cloudflared (Windows).",
                ex);
        }
    }
}
