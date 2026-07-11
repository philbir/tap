using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Core.Cloudflare;

/// <summary>
/// Thin Cloudflare REST client used by the AppHost lifecycle and the CLI to manage
/// tunnels and DNS records. NOT for runtime ingress queries — that lives in Tap.Server.
/// </summary>
public sealed class CloudflareApi(HttpClient http, string apiToken)
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    // Cloudflare's API rejects requests with +-escaped '+' in tunnel_secret
    // ("expected a borrowed string"). Use the relaxed encoder so '+' stays literal.
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = CloudflareApiJson.Default,
    };

    public async Task<CfTunnel?> GetTunnelByNameAsync(string accountId, string name, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get,
            $"{ApiBase}/accounts/{accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false");
        var payload = await SendAsync<CfListResponse<CfTunnel>>(req, ct);
        return payload.Result?.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }

    public async Task<(CfTunnel Tunnel, string TunnelSecret)> CreateTunnelAsync(string accountId, string name, CancellationToken ct)
    {
        // 32 random bytes, base64-encoded — required format for both the API and
        // cloudflared's local credentials file.
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var req = NewRequest(HttpMethod.Post, $"{ApiBase}/accounts/{accountId}/cfd_tunnel");
        req.Content = WriteJsonContent(new CfCreateTunnelRequest(name, secret, "local"));

        var payload = await SendAsync<CfSingleResponse<CfTunnel>>(req, ct);
        var tunnel = payload.Result ?? throw new InvalidOperationException("Cloudflare tunnel create returned no result.");
        return (tunnel, secret);
    }

    public async Task<CfTunnelDetails?> GetTunnelDetailsAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"{ApiBase}/accounts/{accountId}/cfd_tunnel/{tunnelId}");
        var payload = await SendAsync<CfSingleResponse<CfTunnelDetails>>(req, ct);
        return payload.Result;
    }

    /// <summary>
    /// Deletes a tunnel. Cloudflare rejects deletion while connectors are active, so this
    /// first calls the cleanup endpoint to drop stale connections (no-op if the tunnel is idle).
    /// </summary>
    public async Task DeleteTunnelAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        using (var cleanup = NewRequest(HttpMethod.Delete,
            $"{ApiBase}/accounts/{accountId}/cfd_tunnel/{tunnelId}/connections"))
        {
            // Cleanup is best-effort: swallow non-success responses since the delete below
            // will surface the real error if the tunnel is still in use.
            try { using var _ = await http.SendAsync(cleanup, ct); } catch { }
        }

        using var req = NewRequest(HttpMethod.Delete, $"{ApiBase}/accounts/{accountId}/cfd_tunnel/{tunnelId}");
        await SendAsync<CfSingleResponse<CfTunnel>>(req, ct);
    }

    public async Task<string> WriteCredentialsFileAsync(
        string accountId, CfTunnel tunnel, string tunnelSecret, CancellationToken ct)
    {
        var creds = new CfTunnelCredentials(accountId, tunnel.Id, tunnel.Name, tunnelSecret);
        var path = Path.Combine(Path.GetTempPath(), $"cloudflared-{tunnel.Name}-{tunnel.Id}.json");
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, creds, CloudflareApiJson.Default.CfTunnelCredentials, ct);
        return path;
    }

    public async Task<string> GetZoneIdByNameAsync(string zoneName, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"{ApiBase}/zones?name={Uri.EscapeDataString(zoneName)}");
        var payload = await SendAsync<CfListResponse<CfZone>>(req, ct);
        var zone = payload.Result?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Cloudflare zone '{zoneName}' not found (or token lacks zone:read).");
        return zone.Id;
    }

    /// <summary>Names of every zone visible to the current API token (best-effort, for diagnostics).</summary>
    public async Task<IReadOnlyList<string>> ListZoneNamesAsync(CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"{ApiBase}/zones?per_page=50");
        var payload = await SendAsync<CfListResponse<CfZone>>(req, ct);
        return payload.Result?.Select(z => z.Name).ToArray() ?? [];
    }

    /// <summary>Resolve a zone id by walking labels of <paramref name="fqdn"/> upward (a.b.c.d -> b.c.d -> c.d).</summary>
    public async Task<string> ResolveZoneIdForFqdnAsync(string fqdn, CancellationToken ct)
    {
        var labels = fqdn.Split('.');
        var tried = new List<string>();
        for (var i = 1; i < labels.Length - 1; i++)
        {
            var candidate = string.Join('.', labels.Skip(i));
            tried.Add(candidate);
            try { return await GetZoneIdByNameAsync(candidate, ct); } catch { }
        }

        // Nothing matched. The usual cause is a domain/account mismatch (the hostname's zone
        // simply isn't on the account this token is scoped to), so enrich the failure with the
        // zones the token CAN see — that turns a dead-end error into a self-diagnosing one.
        var triedClause = tried.Count > 0 ? $" (tried {string.Join(", ", tried)})" : "";
        throw new InvalidOperationException(
            $"Could not resolve a Cloudflare zone for '{fqdn}'{triedClause}. {await DescribeVisibleZonesAsync(ct)}");
    }

    private async Task<string> DescribeVisibleZonesAsync(CancellationToken ct)
    {
        try
        {
            var zones = await ListZoneNamesAsync(ct);
            return zones.Count == 0
                ? "This API token can see no zones — it likely lacks the Zone:Read permission or is scoped to a different account."
                : $"Zones visible to this API token: {string.Join(", ", zones)}. "
                  + "Use a hostname under one of those, or add the missing domain to this Cloudflare account.";
        }
        catch (Exception ex)
        {
            return $"(Could not list visible zones to help diagnose: {ex.Message})";
        }
    }

    public async Task EnsureDnsCnameAsync(string zoneId, string fqdn, string targetCname, CancellationToken ct)
    {
        using (var listReq = NewRequest(HttpMethod.Get,
            $"{ApiBase}/zones/{zoneId}/dns_records?name={Uri.EscapeDataString(fqdn)}"))
        {
            var existing = await SendAsync<CfListResponse<CfDnsRecord>>(listReq, ct);
            var match = existing.Result?.FirstOrDefault();
            if (match is not null)
            {
                if (string.Equals(match.Type, "CNAME", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(match.Content, targetCname, StringComparison.OrdinalIgnoreCase)
                    && match.Proxied)
                {
                    return;
                }

                using var patch = NewRequest(HttpMethod.Patch, $"{ApiBase}/zones/{zoneId}/dns_records/{match.Id}");
                patch.Content = WriteJsonContent(new CfDnsRecordRequest("CNAME", fqdn, targetCname, true));
                await SendAsync<CfSingleResponse<CfDnsRecord>>(patch, ct);
                return;
            }
        }

        using var create = NewRequest(HttpMethod.Post, $"{ApiBase}/zones/{zoneId}/dns_records");
        create.Content = WriteJsonContent(new CfDnsRecordRequest("CNAME", fqdn, targetCname, true));
        await SendAsync<CfSingleResponse<CfDnsRecord>>(create, ct);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        return req;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
        where T : CfBaseResponse
    {
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Cloudflare API {req.Method} {req.RequestUri?.PathAndQuery} -> {(int)resp.StatusCode}: {text}");
        }

        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)CloudflareApiJson.Default.GetTypeInfo(typeof(T))!;
        var payload = JsonSerializer.Deserialize(text, typeInfo)
            ?? throw new InvalidOperationException("Cloudflare API returned empty body.");
        if (!payload.Success)
        {
            throw new InvalidOperationException(
                $"Cloudflare API {req.Method} {req.RequestUri?.PathAndQuery} reported failure: {text}");
        }
        return payload;
    }

    private static StringContent WriteJsonContent<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, WriteOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}

public abstract class CfBaseResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
}

public sealed class CfListResponse<T> : CfBaseResponse
{
    [JsonPropertyName("result")] public T[]? Result { get; init; }
}

public sealed class CfSingleResponse<T> : CfBaseResponse
{
    [JsonPropertyName("result")] public T? Result { get; init; }
}

public sealed class CfTunnel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("account_tag")] public string? AccountTag { get; init; }
}

public sealed class CfTunnelDetails
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("connections")] public CfTunnelConnection[]? Connections { get; init; }
}

public sealed class CfTunnelConnection
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("colo_name")] public string? Colo { get; init; }
}

public sealed class CfZone
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

public sealed class CfDnsRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("proxied")] public bool Proxied { get; init; }
}

public sealed record CfCreateTunnelRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tunnel_secret")] string TunnelSecret,
    [property: JsonPropertyName("config_src")] string ConfigSrc);

public sealed record CfDnsRecordRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("proxied")] bool Proxied);

public sealed record CfTunnelCredentials(
    [property: JsonPropertyName("AccountTag")] string AccountTag,
    [property: JsonPropertyName("TunnelID")] string TunnelId,
    [property: JsonPropertyName("TunnelName")] string TunnelName,
    [property: JsonPropertyName("TunnelSecret")] string TunnelSecret);

[JsonSerializable(typeof(CfListResponse<CfTunnel>))]
[JsonSerializable(typeof(CfSingleResponse<CfTunnel>))]
[JsonSerializable(typeof(CfSingleResponse<CfTunnelDetails>))]
[JsonSerializable(typeof(CfListResponse<CfZone>))]
[JsonSerializable(typeof(CfListResponse<CfDnsRecord>))]
[JsonSerializable(typeof(CfSingleResponse<CfDnsRecord>))]
[JsonSerializable(typeof(CfCreateTunnelRequest))]
[JsonSerializable(typeof(CfDnsRecordRequest))]
[JsonSerializable(typeof(CfTunnelCredentials))]
internal sealed partial class CloudflareApiJson : JsonSerializerContext;
