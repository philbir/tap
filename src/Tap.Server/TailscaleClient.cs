using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Server;

/// <summary>
/// Configured at startup from <c>Inspector__Tunnel__SocketPath</c>; controls which
/// tailscale socket the inspector talks to. Empty/null means use the system default.
/// </summary>
public sealed class TailscaleOptions
{
    public string? SocketPath { get; init; }
}

/// <summary>
/// Shells out to the `tailscale` CLI to surface daemon + funnel state for the inspector UI.
/// Used only when the inspector was attached to a TailscaleFunnelResource — the AppHost
/// sets <c>Inspector__Provider=tailscale</c> in that case, gating these endpoints.
/// </summary>
public sealed class TailscaleClient(TailscaleOptions options)
{
    public async Task<TailscaleSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var available = await RunAsync(new[] { "version" }, ct);
        if (!available.Ok)
        {
            return new TailscaleSnapshot(
                Available: false,
                Version: null, BackendState: null, Online: false,
                HostName: null, DnsName: null, TailscaleIps: Array.Empty<string>(),
                TailnetName: null, FunnelRules: Array.Empty<TailscaleFunnelRule>(),
                Error: $"tailscale CLI not available: {available.Stderr.Trim()}");
        }

        var statusRes = await RunAsync(new[] { "status", "--json", "--peers=false" }, ct);
        if (!statusRes.Ok)
        {
            return new TailscaleSnapshot(
                Available: true,
                Version: ExtractFirstLine(available.Stdout),
                BackendState: null, Online: false,
                HostName: null, DnsName: null, TailscaleIps: Array.Empty<string>(),
                TailnetName: null, FunnelRules: Array.Empty<TailscaleFunnelRule>(),
                Error: $"`tailscale status` failed: {statusRes.Stderr.Trim()}");
        }

        TailscaleStatusDto? status = null;
        try { status = JsonSerializer.Deserialize(statusRes.Stdout, TailscaleJsonContext.Default.TailscaleStatusDto); }
        catch (JsonException ex)
        {
            return new TailscaleSnapshot(
                Available: true, Version: ExtractFirstLine(available.Stdout),
                BackendState: null, Online: false,
                HostName: null, DnsName: null, TailscaleIps: Array.Empty<string>(),
                TailnetName: null, FunnelRules: Array.Empty<TailscaleFunnelRule>(),
                Error: $"Failed to parse `tailscale status` JSON: {ex.Message}");
        }

        var rules = await GetFunnelRulesAsync(ct);

        return new TailscaleSnapshot(
            Available: true,
            Version: status?.Version ?? ExtractFirstLine(available.Stdout),
            BackendState: status?.BackendState,
            Online: status?.Self?.Online ?? false,
            HostName: status?.Self?.HostName,
            DnsName: status?.Self?.DnsName?.TrimEnd('.'),
            TailscaleIps: status?.Self?.TailscaleIps ?? Array.Empty<string>(),
            TailnetName: status?.CurrentTailnet?.Name ?? status?.MagicDnsSuffix,
            FunnelRules: rules,
            Error: null);
    }

    private async Task<TailscaleFunnelRule[]> GetFunnelRulesAsync(CancellationToken ct)
    {
        var serveRes = await RunAsync(new[] { "serve", "status", "--json" }, ct);
        if (!serveRes.Ok || string.IsNullOrWhiteSpace(serveRes.Stdout)) return Array.Empty<TailscaleFunnelRule>();

        TailscaleServeStatusDto? dto;
        try { dto = JsonSerializer.Deserialize(serveRes.Stdout, TailscaleJsonContext.Default.TailscaleServeStatusDto); }
        catch (JsonException) { return Array.Empty<TailscaleFunnelRule>(); }
        if (dto is null) return Array.Empty<TailscaleFunnelRule>();

        var rules = new List<TailscaleFunnelRule>();
        if (dto.Web is not null)
        {
            foreach (var (hostPort, web) in dto.Web)
            {
                var isFunnel = dto.AllowFunnel?.GetValueOrDefault(hostPort) ?? false;
                if (web?.Handlers is null) continue;
                foreach (var (path, handler) in web.Handlers)
                {
                    rules.Add(new TailscaleFunnelRule(hostPort, path, handler?.Proxy ?? string.Empty, isFunnel));
                }
            }
        }
        return rules.ToArray();
    }

    private async Task<(bool Ok, string Stdout, string Stderr)> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("tailscale")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (!string.IsNullOrEmpty(options.SocketPath))
            {
                psi.ArgumentList.Add($"--socket={options.SocketPath}");
            }
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode == 0, await stdoutTask, await stderrTask);
        }
        catch (Exception ex) { return (false, string.Empty, ex.Message); }
    }

    private static string? ExtractFirstLine(string s)
    {
        var idx = s.IndexOfAny(new[] { '\r', '\n' });
        var first = idx < 0 ? s : s[..idx];
        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }
}

public sealed record TailscaleSnapshot(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("backendState")] string? BackendState,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("hostName")] string? HostName,
    [property: JsonPropertyName("dnsName")] string? DnsName,
    [property: JsonPropertyName("tailscaleIps")] string[] TailscaleIps,
    [property: JsonPropertyName("tailnetName")] string? TailnetName,
    [property: JsonPropertyName("funnelRules")] TailscaleFunnelRule[] FunnelRules,
    [property: JsonPropertyName("error")] string? Error);

public sealed record TailscaleFunnelRule(
    [property: JsonPropertyName("hostPort")] string HostPort,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("upstream")] string Upstream,
    [property: JsonPropertyName("isFunnel")] bool IsFunnel);

internal sealed class TailscaleStatusDto
{
    [JsonPropertyName("Version")] public string? Version { get; init; }
    [JsonPropertyName("BackendState")] public string? BackendState { get; init; }
    [JsonPropertyName("Self")] public TailscaleSelfDto? Self { get; init; }
    [JsonPropertyName("MagicDNSSuffix")] public string? MagicDnsSuffix { get; init; }
    [JsonPropertyName("CurrentTailnet")] public TailscaleTailnetDto? CurrentTailnet { get; init; }
}

internal sealed class TailscaleSelfDto
{
    [JsonPropertyName("HostName")] public string? HostName { get; init; }
    [JsonPropertyName("DNSName")] public string? DnsName { get; init; }
    [JsonPropertyName("Online")] public bool Online { get; init; }
    [JsonPropertyName("TailscaleIPs")] public string[]? TailscaleIps { get; init; }
}

internal sealed class TailscaleTailnetDto
{
    [JsonPropertyName("Name")] public string? Name { get; init; }
}

internal sealed class TailscaleServeStatusDto
{
    [JsonPropertyName("Web")] public Dictionary<string, TailscaleWebHostDto?>? Web { get; init; }
    [JsonPropertyName("AllowFunnel")] public Dictionary<string, bool>? AllowFunnel { get; init; }
}

internal sealed class TailscaleWebHostDto
{
    [JsonPropertyName("Handlers")] public Dictionary<string, TailscaleHandlerDto?>? Handlers { get; init; }
}

internal sealed class TailscaleHandlerDto
{
    [JsonPropertyName("Proxy")] public string? Proxy { get; init; }
}

[JsonSerializable(typeof(TailscaleStatusDto))]
[JsonSerializable(typeof(TailscaleServeStatusDto))]
[JsonSerializable(typeof(TailscaleSnapshot))]
internal sealed partial class TailscaleJsonContext : JsonSerializerContext;
