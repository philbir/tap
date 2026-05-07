using System.ComponentModel;
using Spectre.Console.Cli;

namespace Tap.Cli.Commands;

/// <summary>
/// Common flags shared by <c>tap run</c> and <c>tap save</c>. Positional arguments are
/// declared by each derived settings class to keep their order independent.
/// </summary>
public abstract class TapBaseSettings : CommandSettings
{
    [CommandOption("--proxy-port")]
    [Description("Inspector proxy port (default 4444).")]
    public int ProxyPort { get; init; } = 4444;

    [CommandOption("--ui-port")]
    [Description("Inspector UI port (default 4445).")]
    public int UiPort { get; init; } = 4445;

    [CommandOption("--quick")]
    [Description("Spin up a TryCloudflare quick tunnel and route it through the inspector.")]
    public bool Quick { get; init; }

    [CommandOption("--token <TOKEN>")]
    [Description("Cloudflare connector token (token-mode tunnel). Env: CLOUDFLARE_TUNNEL_TOKEN.")]
    public string? Token { get; init; }

    [CommandOption("--api-token <TOKEN>")]
    [Description("Cloudflare API token (Account:Cloudflare Tunnel:Edit + Zone:DNS:Edit). Env: CLOUDFLARE_API_TOKEN.")]
    public string? ApiToken { get; init; }

    [CommandOption("--account <ID>")]
    [Description("Cloudflare account ID (required for --api-managed and --dynamic). Env: CLOUDFLARE_ACCOUNT_ID.")]
    public string? AccountId { get; init; }

    [CommandOption("--api-managed <NAME>")]
    [Description("Use the Cloudflare API to create/reuse a named tunnel and route HOSTNAME through it.")]
    public string? ApiManagedTunnelName { get; init; }

    [CommandOption("--dynamic <ZONE>")]
    [Description("Mint a fresh hostname under ZONE (uses --api-managed; pair with --account/--api-token).")]
    public string? DynamicZone { get; init; }

    [CommandOption("--hostname <FQDN>")]
    [Description("Public hostname for token/api-managed mode.")]
    public string? Hostname { get; init; }

    [CommandOption("--docker")]
    [Description("Run cloudflared in a Docker container (cloudflare/cloudflared:latest).")]
    public bool Docker { get; init; }

    [CommandOption("--auto-install")]
    [Description("If cloudflared is missing, install it via the host's package manager.")]
    public bool AutoInstall { get; init; }

    [CommandOption("--auth-header <NAME=VALUE>")]
    [Description("Require an API key in this header on the proxy.")]
    public string? AuthHeader { get; init; }

    [CommandOption("--auth-cidr <CIDR>")]
    [Description("Allowlist source CIDR (repeatable).")]
    public string[]? AuthCidrs { get; init; }

    [CommandOption("--auth-country <CC>")]
    [Description("Allowlist ISO country code (CF-IPCountry, repeatable).")]
    public string[]? AuthCountries { get; init; }

    [CommandOption("--auth-oidc-authority <URL>")]
    public string? OidcAuthority { get; init; }
    [CommandOption("--auth-oidc-client-id <ID>")]
    public string? OidcClientId { get; init; }
    [CommandOption("--auth-oidc-client-secret <SECRET>")]
    public string? OidcClientSecret { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("Optional tap.config JSON file to load defaults from.")]
    public string? ConfigPath { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Show ASP.NET Core framework logs alongside the request log.")]
    public bool Verbose { get; init; }
}
