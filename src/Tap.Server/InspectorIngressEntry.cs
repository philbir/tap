using System.Text.Json.Serialization;

namespace Tap.Server;

public sealed record InspectorIngressEntry(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("upstream")] string Upstream);

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InspectorConfig))]
[JsonSerializable(typeof(UpsertHostnameRequest))]
[JsonSerializable(typeof(HostnameResult[]))]
internal sealed partial class InspectorConfigJsonContext : JsonSerializerContext;
