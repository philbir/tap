using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tap.Core.Cloudflare;

namespace Tap.Core.Cloudflared;

public enum CloudflaredHostMode
{
    /// <summary>Spawn the local cloudflared binary (must be on PATH).</summary>
    Process,
    /// <summary>Run cloudflared inside a Docker container (cloudflare/cloudflared:latest).</summary>
    Docker,
}

public sealed class CloudflaredRunSpec
{
    public required CloudflaredHostMode HostMode { get; init; }
    public required ProvisionedTunnel Tunnel { get; init; }
    /// <summary>Inspector proxy URL — when set, all ingress is forwarded through it.</summary>
    public string? InspectorProxyUrl { get; init; }
    /// <summary>For local-ingress modes: where to write the generated config.yml.</summary>
    public string? ConfigYamlPath { get; init; }
    /// <summary>Docker image for docker mode (default cloudflare/cloudflared:latest).</summary>
    public string DockerImage { get; init; } = "cloudflare/cloudflared:latest";
}

/// <summary>
/// Builds the cloudflared invocation (process args / docker args + needed config files).
/// Does NOT manage the process lifetime — that's left to the caller (Aspire's DCP for the
/// Aspire integration, or a long-running Process for the CLI).
/// </summary>
public static class CloudflaredCommand
{
    public sealed record Built(string FileName, IReadOnlyList<string> Args, string? GeneratedConfigPath);

    public static async Task<Built> BuildAsync(CloudflaredRunSpec spec, CancellationToken ct)
    {
        var args = new List<string> { "tunnel", "--no-autoupdate" };

        switch (spec.Tunnel.Mode)
        {
            case TunnelMode.Quick:
                args.Add("--url");
                args.Add(spec.Tunnel.LocalUrl ?? throw new InvalidOperationException("Quick tunnel needs LocalUrl."));
                break;

            case TunnelMode.Token:
                args.Add("run");
                args.Add("--token");
                args.Add(spec.Tunnel.Token ?? throw new InvalidOperationException("Token tunnel needs Token."));
                break;

            case TunnelMode.ApiManaged:
            case TunnelMode.Dynamic:
                if (string.IsNullOrEmpty(spec.Tunnel.TunnelId) || string.IsNullOrEmpty(spec.Tunnel.CredentialsFilePath))
                    throw new InvalidOperationException("Provisioned tunnel missing TunnelId/CredentialsFilePath.");
                var configPath = spec.ConfigYamlPath
                    ?? Path.Combine(Path.GetTempPath(), $"cloudflared-{spec.Tunnel.TunnelName}-{Guid.NewGuid():N}.yml");
                await WriteConfigYamlAsync(configPath, spec, ct);
                args.Add("--config");
                args.Add(configPath);
                args.Add("run");
                args.Add(spec.Tunnel.TunnelId);
                return BuildHostInvocation(spec, args, configPath);

            case TunnelMode.None:
                throw new InvalidOperationException("Cannot build cloudflared command for TunnelMode.None.");
        }

        return BuildHostInvocation(spec, args, null);
    }

    private static Built BuildHostInvocation(CloudflaredRunSpec spec, List<string> args, string? configPath)
    {
        if (spec.HostMode == CloudflaredHostMode.Process)
        {
            return new Built("cloudflared", args, configPath);
        }

        // Docker mode — wrap with `docker run`.
        var dockerArgs = new List<string>
        {
            "run", "--rm",
            "--name", $"tap-cloudflared-{Guid.NewGuid():N}".Substring(0, 24),
            "--network", "host", // so localhost:proxyPort reaches the host's inspector
        };
        if (configPath is not null)
        {
            dockerArgs.Add("-v");
            dockerArgs.Add($"{configPath}:{configPath}:ro");
            if (!string.IsNullOrEmpty(spec.Tunnel.CredentialsFilePath))
            {
                dockerArgs.Add("-v");
                dockerArgs.Add($"{spec.Tunnel.CredentialsFilePath}:{spec.Tunnel.CredentialsFilePath}:ro");
            }
        }
        dockerArgs.Add(spec.DockerImage);
        dockerArgs.AddRange(args);
        return new Built("docker", dockerArgs, configPath);
    }

    private static async Task WriteConfigYamlAsync(string path, CloudflaredRunSpec spec, CancellationToken ct)
    {
        var lines = new List<string>
        {
            $"tunnel: {spec.Tunnel.TunnelId}",
            $"credentials-file: {spec.Tunnel.CredentialsFilePath}",
            "ingress:",
        };
        foreach (var host in spec.Tunnel.Hostnames.Where(h => !string.IsNullOrEmpty(h)))
        {
            // If an inspector is in front, route ALL ingress through it.
            var service = spec.InspectorProxyUrl ?? "http://localhost:80";
            lines.Add($"  - hostname: {host}");
            lines.Add($"    service: {service}");
        }
        lines.Add("  - service: http_status:404");
        await File.WriteAllLinesAsync(path, lines, ct);
    }
}
