using System.Text.Json.Serialization;
using Tap.Core.Cloudflare;

namespace Tap.Core.Profiles;

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

    // Tailscale fields (used when TunnelMode == Tailscale).
    [JsonPropertyName("tailscaleDaemonMode")] public TailscaleDaemonMode? TailscaleDaemonMode { get; init; }
    [JsonPropertyName("tailscaleAuthKey")] public string? TailscaleAuthKey { get; init; }
    [JsonPropertyName("tailscaleLoginServer")] public string? TailscaleLoginServer { get; init; }
    [JsonPropertyName("tailscaleFunnelPort")] public int? TailscaleFunnelPort { get; init; }
    /// <summary>True = `tailscale funnel` (public); false / null = `tailscale serve` (tailnet-only, default).</summary>
    [JsonPropertyName("tailscalePublic")] public bool? TailscalePublic { get; init; }

    [JsonPropertyName("authHeader")] public string? AuthHeader { get; init; }
    [JsonPropertyName("authCidrs")] public string[]? AuthCidrs { get; init; }
    [JsonPropertyName("authCountries")] public string[]? AuthCountries { get; init; }
    [JsonPropertyName("oidcAuthority")] public string? OidcAuthority { get; init; }
    [JsonPropertyName("oidcClientId")] public string? OidcClientId { get; init; }
    [JsonPropertyName("oidcClientSecret")] public string? OidcClientSecret { get; init; }

    /// <summary>
    /// The fields that carry a credential. Anything listed here is redacted before a profile
    /// leaves the machine over HTTP — see <c>Tap.Server.ProfileEndpoints</c>.
    /// </summary>
    public static readonly string[] SecretFieldNames =
        ["token", "apiToken", "tailscaleAuthKey", "oidcClientSecret"];

    /// <summary>Copy with a different <see cref="Name"/>, for when the filename is authoritative.</summary>
    public TunnelProfile WithName(string name)
        => Clone(name, Token, ApiToken, TailscaleAuthKey, OidcClientSecret);

    /// <summary>Copy with the four credential fields replaced (redact on the way out, restore on save).</summary>
    public TunnelProfile WithSecrets(string? token, string? apiToken, string? tailscaleAuthKey, string? oidcClientSecret)
        => Clone(Name, token, apiToken, tailscaleAuthKey, oidcClientSecret);

    /// <summary>
    /// The single place that enumerates every field. Both copy helpers route through it so a
    /// newly added property can't be silently dropped by one caller and kept by another — the
    /// bug that previously lost every <c>tailscale*</c> field on load and on save.
    /// </summary>
    private TunnelProfile Clone(
        string name, string? token, string? apiToken, string? tailscaleAuthKey, string? oidcClientSecret) => new()
        {
            Name = name,
            Upstream = Upstream,
            ProxyPort = ProxyPort,
            UiPort = UiPort,
            TunnelMode = TunnelMode,
            Token = token,
            ApiToken = apiToken,
            AccountId = AccountId,
            ApiManagedTunnelName = ApiManagedTunnelName,
            DynamicZone = DynamicZone,
            Hostname = Hostname,
            Docker = Docker,
            AutoInstall = AutoInstall,
            TailscaleDaemonMode = TailscaleDaemonMode,
            TailscaleAuthKey = tailscaleAuthKey,
            TailscaleLoginServer = TailscaleLoginServer,
            TailscaleFunnelPort = TailscaleFunnelPort,
            TailscalePublic = TailscalePublic,
            AuthHeader = AuthHeader,
            AuthCidrs = AuthCidrs,
            AuthCountries = AuthCountries,
            OidcAuthority = OidcAuthority,
            OidcClientId = OidcClientId,
            OidcClientSecret = oidcClientSecret,
        };
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TunnelProfile))]
[JsonSerializable(typeof(TunnelProfile[]))]
[JsonSerializable(typeof(List<TunnelProfile>))]
[JsonSerializable(typeof(TunnelMode))]
[JsonSerializable(typeof(TailscaleDaemonMode))]
public sealed partial class TunnelProfileJson : JsonSerializerContext;
