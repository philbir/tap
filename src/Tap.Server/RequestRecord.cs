using System.Text.Json.Serialization;

namespace Tap.Server;

public sealed class RequestRecord
{
    public required long Sequence { get; init; }

    public required Guid Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Method { get; init; }

    public required string Host { get; init; }

    public required string Path { get; init; }

    public required string Scheme { get; init; }

    public required string? Upstream { get; init; }

    public required string? RemoteIp { get; init; }

    public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }

    public string? RequestBody { get; set; }

    public bool RequestBodyTruncated { get; set; }

    public long RequestBodyOriginalSize { get; set; }

    public string? RequestContentType { get; set; }

    public int StatusCode { get; set; }

    public IReadOnlyDictionary<string, string> ResponseHeaders { get; set; } =
        new Dictionary<string, string>();

    public string? ResponseBody { get; set; }

    public string? ResponseBodyBase64 { get; set; }

    public bool ResponseBodyTruncated { get; set; }

    public long ResponseBodyOriginalSize { get; set; }

    public string? ResponseContentType { get; set; }

    public long DurationMs { get; set; }

    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RequestRecord))]
[JsonSerializable(typeof(List<RequestRecord>))]
internal sealed partial class RequestRecordJsonContext : JsonSerializerContext;
