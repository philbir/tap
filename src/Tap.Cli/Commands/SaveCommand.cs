using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Core.Cloudflare;
using Tap.Core.Profiles;
using Tap.Core.Cloudflared;

namespace Tap.Cli.Commands;

public sealed class SaveCommand : Command<SaveCommand.Settings>
{
    public sealed class Settings : TapBaseSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Profile name. Stored as ~/.tap/tunnels/<name>.json.")]
        public string ProfileName { get; init; } = "";

        [CommandArgument(1, "[upstream]")]
        [Description("Upstream URL.")]
        public string? Upstream { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var store = new TunnelProfileStore();

        // Re-use the run-command merge so env/config still apply.
        var existing = store.Load(settings.ProfileName);
        var merged = RunCommand.MergeWithEnvAndConfig(settings, settings.Upstream, existing);

        var profile = new TunnelProfile
        {
            Name = settings.ProfileName,
            Upstream = merged.Upstream,
            ProxyPort = merged.ProxyPort,
            UiPort = merged.UiPort,
            TunnelMode = ResolveTunnelMode(merged),
            Token = merged.Token,
            ApiToken = merged.ApiToken,
            AccountId = merged.AccountId,
            ApiManagedTunnelName = merged.ApiManagedTunnelName,
            DynamicZone = merged.DynamicZone,
            Hostname = merged.Hostname,
            Docker = merged.HostMode == CloudflaredHostMode.Docker
                || merged.TailscaleHostMode == TailscaleHostMode.Docker,
            AutoInstall = merged.AutoInstall,
            // Tailscale fields: prefer the merged values (CLI flags + env override profile),
            // fall back to whatever was in `existing` for fields the CLI doesn't expose
            // (auth key, login server, port).
            TailscaleDaemonMode = merged.Tailscale ? merged.TailscaleDaemonMode : existing?.TailscaleDaemonMode,
            TailscaleAuthKey = merged.TailscaleAuthKey ?? existing?.TailscaleAuthKey,
            TailscaleLoginServer = merged.TailscaleLoginServer ?? existing?.TailscaleLoginServer,
            TailscaleFunnelPort = merged.Tailscale ? merged.TailscaleFunnelPort : existing?.TailscaleFunnelPort,
            TailscalePublic = merged.Tailscale ? merged.TailscalePublic : existing?.TailscalePublic,
            AuthHeader = merged.AuthHeader,
            AuthCidrs = merged.AuthCidrs,
            AuthCountries = merged.AuthCountries,
            OidcAuthority = merged.OidcAuthority,
            OidcClientId = merged.OidcClientId,
            OidcClientSecret = merged.OidcClientSecret,
        };

        store.Save(profile);
        AnsiConsole.MarkupLine($"[green]Saved[/] [bold]{Markup.Escape(profile.Name)}[/] [grey]→[/] {Markup.Escape(Path.Combine(store.RootDirectory, profile.Name + ".json"))}");
        AnsiConsole.MarkupLine($"[grey]Run with:[/] tap run --name {Markup.Escape(profile.Name)}");
        return 0;
    }

    private static Tap.Core.Cloudflare.TunnelMode ResolveTunnelMode(RunCommand.MergedSettings m)
    {
        if (m.Tailscale) return Tap.Core.Cloudflare.TunnelMode.Tailscale;
        if (m.Quick) return Tap.Core.Cloudflare.TunnelMode.Quick;
        if (!string.IsNullOrWhiteSpace(m.Token)) return Tap.Core.Cloudflare.TunnelMode.Token;
        if (!string.IsNullOrWhiteSpace(m.DynamicZone)) return Tap.Core.Cloudflare.TunnelMode.Dynamic;
        if (!string.IsNullOrWhiteSpace(m.ApiManagedTunnelName)) return Tap.Core.Cloudflare.TunnelMode.ApiManaged;
        return Tap.Core.Cloudflare.TunnelMode.None;
    }
}
