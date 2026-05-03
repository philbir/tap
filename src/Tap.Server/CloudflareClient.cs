using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Tap.Server;

public sealed class CloudflareOptions
{
    public string? ApiToken { get; init; }
    public string? AccountId { get; init; }
    public string? TunnelId { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiToken)
        && !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(TunnelId);
}

public sealed class CloudflareClient(HttpClient http, CloudflareOptions options, ILogger<CloudflareClient> logger)
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    public async Task<IngressRule[]> GetIngressAsync(CancellationToken ct)
    {
        EnsureConfigured();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/accounts/{options.AccountId}/cfd_tunnel/{options.TunnelId}/configurations");
        Authorize(req);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync(CloudflareJson.Default.TunnelConfigResponse, ct);
        return payload?.Result?.Config?.Ingress ?? [];
    }

    public async Task<IngressRule[]> UpsertIngressAsync(string hostname, string service, CancellationToken ct)
    {
        EnsureConfigured();

        var current = await GetIngressAsync(ct);

        var rules = current.Where(r => !string.IsNullOrEmpty(r.Hostname)
                                       && !string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase)).ToList();

        rules.Add(new IngressRule { Hostname = hostname, Service = service });
        rules.Add(new IngressRule { Service = "http_status:404" });

        var body = new TunnelConfigRequest(new TunnelConfig([.. rules]));

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{ApiBase}/accounts/{options.AccountId}/cfd_tunnel/{options.TunnelId}/configurations");
        Authorize(req);
        req.Content = JsonContent.Create(body, CloudflareJson.Default.TunnelConfigRequest);

        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Cloudflare API rejected ingress upsert: {Status} {Body}", (int)resp.StatusCode, text);
            throw new InvalidOperationException($"Cloudflare API error ({(int)resp.StatusCode}): {text}");
        }

        return await GetIngressAsync(ct);
    }

    private void Authorize(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
    }

    private void EnsureConfigured()
    {
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Cloudflare API is not configured. Set Cloudflare:ApiToken, Cloudflare:AccountId, and Cloudflare:TunnelId.");
        }
    }
}

public sealed class IngressRule
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("service")]
    public string Service { get; init; } = "http_status:404";

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

public sealed record TunnelConfigRequest(
    [property: JsonPropertyName("config")] TunnelConfig Config);

public sealed record TunnelConfig(
    [property: JsonPropertyName("ingress")] IngressRule[] Ingress);

public sealed class TunnelConfigResponse
{
    [JsonPropertyName("result")]
    public TunnelConfigResult? Result { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }
}

public sealed class TunnelConfigResult
{
    [JsonPropertyName("config")]
    public TunnelConfig? Config { get; init; }
}

[JsonSerializable(typeof(TunnelConfigRequest))]
[JsonSerializable(typeof(TunnelConfigResponse))]
[JsonSerializable(typeof(IngressRule[]))]
internal sealed partial class CloudflareJson : JsonSerializerContext;
