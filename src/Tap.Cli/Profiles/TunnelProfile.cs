using System.Text.Json.Serialization;
using Tap.Core.Cloudflare;

namespace Tap.Cli.Profiles;

/// <summary>
/// A persisted tunnel profile. Loaded by <c>tap run --name &lt;NAME&gt;</c>; saved by
/// <c>tap save &lt;NAME&gt;</c>. Stored as JSON at <see cref="TunnelProfileStore.RootDirectory"/>.
/// </summary>
public sealed class TunnelProfile
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("upstream")] public string? Upstream { get; init; }
    [JsonPropertyName("proxyPort")] public int? ProxyPort { get; init; }
    [JsonPropertyName("uiPort")] public int? UiPort { get; init; }

    [JsonPropertyName("tunnelMode")] public TunnelMode TunnelMode { get; init; } = TunnelMode.None;
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("apiToken")] public string? ApiToken { get; init; }
    [JsonPropertyName("accountId")] public string? AccountId { get; init; }
    [JsonPropertyName("apiManagedTunnelName")] public string? ApiManagedTunnelName { get; init; }
    [JsonPropertyName("dynamicZone")] public string? DynamicZone { get; init; }
    [JsonPropertyName("hostname")] public string? Hostname { get; init; }

    [JsonPropertyName("docker")] public bool Docker { get; init; }
    [JsonPropertyName("autoInstall")] public bool AutoInstall { get; init; }

    [JsonPropertyName("authHeader")] public string? AuthHeader { get; init; }
    [JsonPropertyName("authCidrs")] public string[]? AuthCidrs { get; init; }
    [JsonPropertyName("authCountries")] public string[]? AuthCountries { get; init; }
    [JsonPropertyName("oidcAuthority")] public string? OidcAuthority { get; init; }
    [JsonPropertyName("oidcClientId")] public string? OidcClientId { get; init; }
    [JsonPropertyName("oidcClientSecret")] public string? OidcClientSecret { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TunnelProfile))]
[JsonSerializable(typeof(TunnelMode))]
internal sealed partial class TunnelProfileJson : JsonSerializerContext;
