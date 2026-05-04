using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Tap.Core.Cloudflare;

/// <summary>The kind of tunnel a caller wants.</summary>
public enum TunnelMode
{
    /// <summary>No tunnel — direct local capture.</summary>
    None,
    /// <summary>cloudflared --url &lt;localUrl&gt; (TryCloudflare).</summary>
    Quick,
    /// <summary>cloudflared run --token (you manage tunnel + DNS in the dashboard).</summary>
    Token,
    /// <summary>API creates/reuses a named tunnel and CNAME(s).</summary>
    ApiManaged,
    /// <summary>Same as ApiManaged but hostname is minted on each run.</summary>
    Dynamic,
}

public sealed class TunnelSpec
{
    public required TunnelMode Mode { get; init; }
    public string? Token { get; init; }
    public string? ApiToken { get; init; }
    public string? AccountId { get; init; }
    public string? TunnelName { get; init; }
    public string? LocalUrl { get; init; }
    public List<string> Hostnames { get; } = [];
    public string? DynamicZoneName { get; init; }
    public string? DynamicHostnamePrefix { get; init; } = "svc-";
    public string? DynamicHostnameSuffix { get; init; } = "";
}

public sealed class ProvisionedTunnel
{
    public required TunnelMode Mode { get; init; }
    public string? Token { get; init; }
    public string? TunnelId { get; init; }
    public string? AccountId { get; init; }
    public string? TunnelName { get; init; }
    public string? CredentialsFilePath { get; init; }
    public string? LocalUrl { get; init; }
    public List<string> Hostnames { get; init; } = [];
}

/// <summary>
/// Pure provisioning: given a <see cref="TunnelSpec"/>, ensure the tunnel exists in Cloudflare,
/// write credentials, and create DNS records. Does NOT spawn cloudflared.
/// </summary>
public sealed class TunnelProvisioner(ILogger<TunnelProvisioner> logger)
{
    public async Task<ProvisionedTunnel> ProvisionAsync(TunnelSpec spec, CancellationToken ct)
    {
        switch (spec.Mode)
        {
            case TunnelMode.None:
                return new ProvisionedTunnel { Mode = TunnelMode.None };

            case TunnelMode.Quick:
                if (string.IsNullOrWhiteSpace(spec.LocalUrl))
                    throw new ArgumentException("LocalUrl is required for quick tunnels.");
                return new ProvisionedTunnel { Mode = TunnelMode.Quick, LocalUrl = spec.LocalUrl };

            case TunnelMode.Token:
                if (string.IsNullOrWhiteSpace(spec.Token))
                    throw new ArgumentException("Token is required for token tunnels.");
                CloudflareTokenDecoder.TryDecode(spec.Token, out var decoded);
                return new ProvisionedTunnel
                {
                    Mode = TunnelMode.Token,
                    Token = spec.Token,
                    AccountId = decoded.AccountId,
                    TunnelId = decoded.TunnelId,
                    Hostnames = [.. spec.Hostnames],
                };

            case TunnelMode.ApiManaged:
            case TunnelMode.Dynamic:
                return await ProvisionApiManagedAsync(spec, ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(spec.Mode), spec.Mode, "Unknown tunnel mode.");
        }
    }

    private async Task<ProvisionedTunnel> ProvisionApiManagedAsync(TunnelSpec spec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.ApiToken))
            throw new ArgumentException("ApiToken is required for api-managed/dynamic tunnels.");
        if (string.IsNullOrWhiteSpace(spec.AccountId))
            throw new ArgumentException("AccountId is required for api-managed/dynamic tunnels.");
        if (string.IsNullOrWhiteSpace(spec.TunnelName))
            throw new ArgumentException("TunnelName is required for api-managed/dynamic tunnels.");

        using var http = new HttpClient();
        var api = new CloudflareApi(http, spec.ApiToken);

        // Reuse-or-create.
        var existing = await api.GetTunnelByNameAsync(spec.AccountId, spec.TunnelName, ct);
        CfTunnel cfTunnel;
        string credsPath;
        if (existing is not null)
        {
            cfTunnel = existing;
            credsPath = ExistingCredentialsPathOrThrow(cfTunnel);
            logger.LogInformation("Reusing Cloudflare tunnel '{Name}' ({Id}).", cfTunnel.Name, cfTunnel.Id);
        }
        else
        {
            var (created, secret) = await api.CreateTunnelAsync(spec.AccountId, spec.TunnelName, ct);
            cfTunnel = created;
            credsPath = await api.WriteCredentialsFileAsync(spec.AccountId, cfTunnel, secret, ct);
            logger.LogInformation("Created Cloudflare tunnel '{Name}' ({Id}). Credentials: {Path}",
                cfTunnel.Name, cfTunnel.Id, credsPath);
        }

        // Mint hostnames for dynamic mode (one per blank slot in the spec).
        var finalHostnames = new List<string>(spec.Hostnames);
        if (spec.Mode == TunnelMode.Dynamic && spec.DynamicZoneName is not null)
        {
            // If caller passed no static hostnames, mint one fresh.
            if (finalHostnames.Count == 0)
            {
                finalHostnames.Add(MintDynamicHostname(spec));
            }
            else
            {
                for (var i = 0; i < finalHostnames.Count; i++)
                {
                    if (string.IsNullOrEmpty(finalHostnames[i]))
                    {
                        finalHostnames[i] = MintDynamicHostname(spec);
                    }
                }
            }
        }

        // DNS — ensure each hostname's CNAME points at <tunnelId>.cfargotunnel.com.
        var target = $"{cfTunnel.Id}.cfargotunnel.com";
        foreach (var fqdn in finalHostnames.Where(h => !string.IsNullOrEmpty(h)))
        {
            var zoneId = await api.ResolveZoneIdForFqdnAsync(fqdn, ct);
            await api.EnsureDnsCnameAsync(zoneId, fqdn, target, ct);
            logger.LogInformation("DNS CNAME ensured: {Host} -> {Target} (proxied).", fqdn, target);
        }

        return new ProvisionedTunnel
        {
            Mode = spec.Mode,
            TunnelId = cfTunnel.Id,
            AccountId = spec.AccountId,
            TunnelName = cfTunnel.Name,
            CredentialsFilePath = credsPath,
            Hostnames = finalHostnames,
        };
    }

    private static string ExistingCredentialsPathOrThrow(CfTunnel cfTunnel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cloudflared-{cfTunnel.Name}-{cfTunnel.Id}.json");
        if (File.Exists(path)) return path;

        throw new InvalidOperationException(
            $"Cloudflare tunnel '{cfTunnel.Name}' ({cfTunnel.Id}) already exists but no local credentials file "
            + $"was found at '{path}'. Delete the tunnel in the Cloudflare dashboard (or `cloudflared tunnel delete`) "
            + "to let this tool recreate it with fresh credentials.");
    }

    private static string MintDynamicHostname(TunnelSpec spec)
    {
        var prefix = spec.DynamicHostnamePrefix ?? "svc-";
        var suffix = spec.DynamicHostnameSuffix ?? "";
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var rand = Convert.ToHexStringLower(bytes);
        return $"{prefix}{rand}{suffix}.{spec.DynamicZoneName}";
    }
}
