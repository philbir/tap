using System.Text.Json.Serialization;

namespace Tap.Server;

public sealed record InspectorIngressEntry(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("upstream")] string Upstream)
{
    [JsonPropertyName("tunnelMode")]
    public string? TunnelMode { get; init; }

    [JsonPropertyName("tunnelName")]
    public string? TunnelName { get; init; }

    [JsonPropertyName("publicUrl")]
    public string? PublicUrl { get; init; }
}

[JsonSerializable(typeof(InspectorIngressEntry))]
[JsonSerializable(typeof(InspectorIngressEntry[]))]
[JsonSerializable(typeof(List<InspectorIngressEntry>))]
internal sealed partial class InspectorIngressJsonContext : JsonSerializerContext;

public sealed record InspectorConfig(
    [property: JsonPropertyName("proxyPort")] int ProxyPort,
    [property: JsonPropertyName("ingress")] InspectorIngressEntry[] Ingress,
    [property: JsonPropertyName("apiMode")] string ApiMode,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record UpsertHostnameRequest(
    [property: JsonPropertyName("hostname")] string Hostname);

public sealed record HostnameResult(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("service")] string Service);

public sealed record TunnelContext(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("resourceName")] string ResourceName,
    [property: JsonPropertyName("publicUrl")] string? PublicUrl,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("tunnelId")] string? TunnelId,
    [property: JsonPropertyName("dashboardUrl")] string? DashboardUrl,
    [property: JsonPropertyName("apiResolved")] bool ApiResolved,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("connections")] int? Connections,
    [property: JsonPropertyName("error")] string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InspectorConfig))]
[JsonSerializable(typeof(UpsertHostnameRequest))]
[JsonSerializable(typeof(HostnameResult[]))]
[JsonSerializable(typeof(TunnelContext))]
internal sealed partial class InspectorConfigJsonContext : JsonSerializerContext;
