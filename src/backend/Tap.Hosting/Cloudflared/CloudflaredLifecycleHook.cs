using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tap.Core.Cloudflare;
using Tap.Core.Cloudflared;

namespace Aspire.Hosting.Cloudflared;

#pragma warning disable CS0618 // Eventing-based replacement is not yet wired; lifecycle hook works fine today.
internal sealed class CloudflaredLifecycleHook(
    ILogger<CloudflaredLifecycleHook> logger,
    ResourceLoggerService resourceLoggers,
    ResourceNotificationService resourceNotifications)
    : IDistributedApplicationLifecycleHook
{
    private static readonly Regex QuickTunnelUrlPattern =
        new(@"https?://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        var tunnels = appModel.Resources.OfType<CloudflaredTunnelResource>().ToList();
        if (tunnels.Count == 0)
        {
            return;
        }

        await EnsureCloudflaredAvailableAsync(tunnels, cancellationToken);

        foreach (var tunnel in tunnels)
        {
            await ProvisionAsync(tunnel, cancellationToken);
            ValidateConfigured(tunnel);
            LogSummary(tunnel);

            if (tunnel.IsQuickTunnel)
            {
                // Watcher runs for the lifetime of the AppHost; we don't await it.
                _ = WatchQuickTunnelLogsAsync(tunnel, cancellationToken);
            }
        }
    }
#pragma warning restore CS0618

    private async Task WatchQuickTunnelLogsAsync(CloudflaredTunnelResource tunnel, CancellationToken ct)
    {
        try
        {
            await foreach (var batch in resourceLoggers.WatchAsync(tunnel).WithCancellation(ct))
            {
                foreach (var line in batch)
                {
                    var match = QuickTunnelUrlPattern.Match(line.Content);
                    if (!match.Success) continue;

                    var url = match.Value;
                    if (string.Equals(tunnel.QuickPublicUrl, url, StringComparison.Ordinal))
                    {
                        return;
                    }

                    tunnel.QuickPublicUrl = url;
                    logger.LogInformation("Quick tunnel '{Name}' is live: {Url}", tunnel.Name, url);

                    await resourceNotifications.PublishUpdateAsync(tunnel, snapshot =>
                    {
                        var existing = snapshot.Urls.FirstOrDefault(u => u.Name == "trycloudflare");
                        var newUrl = new UrlSnapshot(Name: "trycloudflare", Url: url, IsInternal: false);
                        var urls = existing is not null
                            ? snapshot.Urls.Replace(existing, newUrl)
                            : snapshot.Urls.Add(newUrl);
                        return snapshot with { Urls = urls };
                    });

                    // Patch the attached tap's "proxy" URL chip so it points to the
                    // freshly-discovered public URL too (the WithUrlForEndpoint callback
                    // already ran with QuickPublicUrl == null).
                    if (tunnel.AttachedTap is { } tap)
                    {
                        await resourceNotifications.PublishUpdateAsync(tap.Resource, snapshot =>
                        {
                            var proxy = snapshot.Urls.FirstOrDefault(u => u.Name == "proxy");
                            if (proxy is null) return snapshot;
                            var patched = proxy with
                            {
                                Url = url,
                                DisplayProperties = new UrlDisplayPropertiesSnapshot("Public URL", 0),
                            };
                            return snapshot with { Urls = snapshot.Urls.Replace(proxy, patched) };
                        });
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to watch quick tunnel '{Name}' logs.", tunnel.Name);
        }
    }

    private async Task ProvisionAsync(CloudflaredTunnelResource tunnel, CancellationToken ct)
    {
        if (tunnel.IsQuickTunnel || tunnel.ApiToken is null)
        {
            return;
        }

        using var http = new HttpClient();
        var api = new CloudflareApi(http, tunnel.ApiToken);

        // Resolve or create the named tunnel.
        var existing = await api.GetTunnelByNameAsync(tunnel.AccountId!, tunnel.ApiTunnelName!, ct);
        CfTunnel cfTunnel;
        string credsPath;

        if (existing is not null)
        {
            var existingPath = LocalCredentialsPath(existing);
            if (File.Exists(existingPath))
            {
                cfTunnel = existing;
                credsPath = existingPath;
                logger.LogInformation("Reusing Cloudflare tunnel '{Name}' ({Id}).", cfTunnel.Name, cfTunnel.Id);
            }
            else if (!tunnel.HasAnnotationOfType<CloudflaredRecreateOnMissingCredentialsAnnotation>())
            {
                // The remote tunnel exists but we have no local TunnelSecret to authenticate
                // cloudflared with (the API never returns the secret for existing tunnels).
                // The only automatic recovery is destructive — deleting a tunnel that may be
                // shared with other machines, CI, or a colleague's dashboard config — so we
                // refuse and let the operator decide, exactly as the CLI does.
                throw new InvalidOperationException(
                    $"Cloudflare tunnel '{existing.Name}' ({existing.Id}) already exists but no local credentials "
                    + $"file was found at '{existingPath}'. Cloudflare never returns an existing tunnel's secret, so "
                    + "Tap cannot connect to it. Delete the tunnel in the Cloudflare dashboard (or `cloudflared tunnel "
                    + $"delete {existing.Name}`) and re-run, use a different tunnel name, or opt in to automatic "
                    + "recreation with .WithApiManagedTunnel(..., recreateOnMissingCredentials: true).");
            }
            else
            {
                logger.LogWarning(
                    "Cloudflare tunnel '{Name}' ({Id}) exists remotely but no local credentials file at '{Path}'. Deleting and recreating (recreateOnMissingCredentials was opted in).",
                    existing.Name, existing.Id, existingPath);
                await api.DeleteTunnelAsync(tunnel.AccountId!, existing.Id, ct);

                var (created, secret) = await api.CreateTunnelAsync(tunnel.AccountId!, tunnel.ApiTunnelName!, ct);
                cfTunnel = created;
                credsPath = await api.WriteCredentialsFileAsync(tunnel.AccountId!, cfTunnel, secret, ct);
                logger.LogInformation("Recreated Cloudflare tunnel '{Name}' ({Id}). Credentials: {Path}",
                    cfTunnel.Name, cfTunnel.Id, credsPath);
            }
        }
        else
        {
            var (created, secret) = await api.CreateTunnelAsync(tunnel.AccountId!, tunnel.ApiTunnelName!, ct);
            cfTunnel = created;
            credsPath = await api.WriteCredentialsFileAsync(tunnel.AccountId!, cfTunnel, secret, ct);
            logger.LogInformation("Created Cloudflare tunnel '{Name}' ({Id}). Credentials: {Path}",
                cfTunnel.Name, cfTunnel.Id, credsPath);
        }

        tunnel.TunnelId = cfTunnel.Id;
        tunnel.CredentialsFilePath = credsPath;

        // Resolve dynamic-host zone id if needed. The configured DynamicZoneName is the
        // suffix where hostnames are minted (e.g. "tap.p7e.dev"), but the actual Cloudflare
        // zone may be a parent of it (e.g. "p7e.dev"). Walk labels until one resolves.
        if (tunnel.DynamicZoneName is not null && string.IsNullOrEmpty(tunnel.DynamicZoneId))
        {
            tunnel.DynamicZoneId = await ResolveZoneIdForFqdnAsync(api, $"_.{tunnel.DynamicZoneName}", ct);
        }

        // Mint hostnames for entries that don't have one yet (dynamic mode).
        if (tunnel.DynamicZoneName is not null)
        {
            foreach (var entry in tunnel.IngressEntries.Where(e => string.IsNullOrEmpty(e.Hostname)))
            {
                var prefix = tunnel.DynamicHostnamePrefix ?? "svc-";
                var suffix = tunnel.DynamicHostnameSuffix ?? string.Empty;
                var label = $"{prefix}{RandomLabel()}{suffix}";
                entry.Hostname = $"{label}.{tunnel.DynamicZoneName}";
                logger.LogInformation("Minted dynamic hostname '{Host}' for resource '{Resource}'.",
                    entry.Hostname, entry.Target.Name);
            }
        }

        // Sync hostnames into any annotations that were left blank at registration time.
        foreach (var entry in tunnel.IngressEntries)
        {
            foreach (var ann in entry.Target.Annotations.OfType<CloudflareTunnelAnnotation>())
            {
                if (string.IsNullOrEmpty(ann.Hostname) && ReferenceEquals(ann.Tunnel, tunnel))
                {
                    ann.Hostname = entry.Hostname ?? string.Empty;
                }
            }
        }

        // Make sure the tap's ingress entries (if any) match the final hostnames.
        if (tunnel.AttachedTap is { } tap)
        {
            var mode = TunnelModeOf(tunnel);
            tap.Annotation.Entries.RemoveAll(e =>
                tunnel.IngressEntries.Any(t => ReferenceEquals(t.Target, e.Target)));
            foreach (var entry in tunnel.IngressEntries)
            {
                tap.Annotation.Entries.Add(new TapIngressEntry(entry.Hostname, entry.Target, entry.EndpointName)
                {
                    TunnelMode = mode,
                    TunnelName = tunnel.ApiTunnelName ?? tunnel.Name,
                    PublicUrl = string.IsNullOrEmpty(entry.Hostname) ? null : $"https://{entry.Hostname}",
                    PublicExpose = true,
                });
            }
        }

        // Ensure DNS CNAMEs exist for every hostname.
        if (tunnel.ManageDns)
        {
            string? zoneIdForDns = tunnel.DynamicZoneId;
            foreach (var entry in tunnel.IngressEntries)
            {
                if (string.IsNullOrEmpty(entry.Hostname)) continue;

                var zoneId = zoneIdForDns ?? await ResolveZoneIdForFqdnAsync(api, entry.Hostname, ct);
                var target = $"{cfTunnel.Id}.cfargotunnel.com";
                // EnsureDnsCnameAsync refuses to convert a record Tap did not create, so a
                // pre-existing A record or a CNAME to somewhere else surfaces as an error here
                // rather than quietly taking the user's hostname over.
                await api.EnsureDnsCnameAsync(zoneId, entry.Hostname, target, ct);
                logger.LogInformation("DNS CNAME ensured: {Host} -> {Target} (proxied).", entry.Hostname, target);
            }
        }
    }

    // Must agree with CloudflareApi.WriteCredentialsFileAsync: if the two ever disagree, a named
    // tunnel looks credential-less on every start and gets deleted and recreated.
    private static string LocalCredentialsPath(CfTunnel cfTunnel) =>
        CloudflareApi.CredentialsPathFor(cfTunnel.Name, cfTunnel.Id);

    private static async Task<string> ResolveZoneIdForFqdnAsync(CloudflareApi api, string fqdn, CancellationToken ct)
    {
        // Walk up labels: a.b.example.com -> b.example.com -> example.com.
        var labels = fqdn.Split('.');
        for (var i = 1; i < labels.Length - 1; i++)
        {
            var candidate = string.Join('.', labels.Skip(i));
            try { return await api.GetZoneIdByNameAsync(candidate, ct); } catch { }
        }
        throw new InvalidOperationException($"Could not resolve a Cloudflare zone for '{fqdn}'.");
    }

    private static string RandomLabel()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static string TunnelModeOf(CloudflaredTunnelResource tunnel)
    {
        if (tunnel.IsQuickTunnel) return "quick";
        if (!tunnel.UseLocalIngress) return "existing";
        if (tunnel.DynamicZoneName is not null) return "dynamic";
        if (tunnel.ApiToken is not null) return "api-managed";
        return "local";
    }

    private static void ValidateConfigured(CloudflaredTunnelResource tunnel)
    {
        if (tunnel.IsQuickTunnel)
        {
            if (string.IsNullOrWhiteSpace(tunnel.QuickLocalUrl))
            {
                throw new InvalidOperationException(
                    $"Cloudflared tunnel '{tunnel.Name}' is in quick mode but no local URL was set. Call .WithQuickTunnel(\"http://localhost:...\").");
            }
            return;
        }

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
                $"Cloudflared tunnel '{tunnel.Name}' has no token. Call .WithExistingTunnel(\"...\"), .WithLocalIngress(...), .WithApiManagedTunnel(...), or .WithQuickTunnel(...).");
        }
    }

    private void LogSummary(CloudflaredTunnelResource tunnel)
    {
        var mode = TunnelModeOf(tunnel);

        if (tunnel.IsQuickTunnel)
        {
            logger.LogInformation(
                "Cloudflared tunnel '{Name}' configured in QUICK mode. Forwarding to {Local}. Public URL will be assigned on startup.",
                tunnel.Name, tunnel.QuickLocalUrl);
            if (tunnel.InspectorProxyPort is { } p)
            {
                logger.LogInformation("HTTP Inspector enabled. UI: http://localhost:{Ui}  |  Proxy port: {Proxy}",
                    tunnel.InspectorUiPort, p);
            }
            return;
        }

        logger.LogInformation(
            "Cloudflared tunnel '{Name}' configured with {Count} ingress entr{Suffix} ({Mode}).",
            tunnel.Name,
            tunnel.IngressEntries.Count,
            tunnel.IngressEntries.Count == 1 ? "y" : "ies",
            mode);

        foreach (var entry in tunnel.IngressEntries)
        {
            logger.LogInformation("  ingress: https://{Hostname} -> {Target}", entry.Hostname, entry.Target.Name);
        }

        if (tunnel.InspectorProxyPort is { } inspectorPort)
        {
            logger.LogInformation(
                "HTTP Inspector enabled. UI: http://localhost:{Ui}  |  Proxy port: {Proxy}",
                tunnel.InspectorUiPort,
                inspectorPort);

            if (mode == "existing")
            {
                foreach (var entry in tunnel.IngressEntries)
                {
                    logger.LogInformation(
                        "Existing-tunnel mode: point {Hostname} -> http://localhost:{Proxy} in the Cloudflare dashboard so traffic flows through the tap.",
                        entry.Hostname,
                        inspectorPort);
                }
            }
        }
    }

    private async Task EnsureCloudflaredAvailableAsync(List<CloudflaredTunnelResource> tunnels, CancellationToken ct)
    {
        // For docker-mode tunnels we only need the docker CLI; we don't enforce that here.
        var anyProcess = tunnels.Any(t => t.HostMode == CloudflaredHostMode.Process);
        if (!anyProcess) return;

        var installer = new CloudflaredInstaller(NullLogger<CloudflaredInstaller>.Instance);
        var autoInstall = tunnels.Any(t => t.HostMode == CloudflaredHostMode.Process && t.AutoInstall);
        var status = await installer.EnsureAvailableAsync(autoInstall, ct);
        if (status == CloudflaredAvailability.Installed)
        {
            logger.LogInformation("cloudflared was missing — installed via host package manager.");
        }
    }
}
