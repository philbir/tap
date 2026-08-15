using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Core.Profiles;
using Tap.Core.Auth;
using Tap.Core.Cloudflare;
using Tap.Core.Cloudflared;
using Tap.Server;

namespace Tap.Cli.Commands;

public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : TapBaseSettings
    {
        [CommandArgument(0, "[upstream]")]
        [Description("Upstream URL to inspect/forward to (e.g. http://localhost:3000). May also be set via tap.config or env TAP_UPSTREAM.")]
        public string? Upstream { get; init; }

        [CommandOption("-n|--name <NAME>")]
        [Description("Run a named profile from ~/.tap/tunnels. CLI flags override profile values.")]
        public string? Name { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Wire Ctrl+C and 'q' to a single cancellation source so background tasks, the
        // request-stream subscription, and the inspector all stop cooperatively.
        // Use PosixSignalRegistration (works regardless of tty/redirection) plus
        // Console.CancelKeyPress as a belt-and-suspenders fallback.
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; cts.Cancel(); });
        var store = new TunnelProfileStore();

        // No upstream + no --name → list saved profiles and exit. This is the "list named tunnels"
        // affordance the README points users to with `tap run`.
        if (string.IsNullOrWhiteSpace(settings.Upstream) && string.IsNullOrWhiteSpace(settings.Name))
        {
            return ListProfiles(store);
        }

        TunnelProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(settings.Name))
        {
            profile = store.Load(settings.Name);
            if (profile is null)
            {
                AnsiConsole.MarkupLine($"[red]No saved profile named '{Markup.Escape(settings.Name)}'.[/]");
                AnsiConsole.MarkupLine($"[grey]Save one with:[/] tap save {Markup.Escape(settings.Name)} <upstream> [[opts]]");
                AnsiConsole.MarkupLine($"[grey]Profiles dir:[/] {Markup.Escape(store.RootDirectory)}");
                return 2;
            }
            AnsiConsole.MarkupLine($"[grey]Loaded profile[/] [bold]{Markup.Escape(profile.Name)}[/] [grey]from {Markup.Escape(store.RootDirectory)}[/]");
        }

        var merged = MergeWithEnvAndConfig(settings, settings.Upstream, profile);
        if (string.IsNullOrWhiteSpace(merged.Upstream))
        {
            AnsiConsole.MarkupLine("[red]upstream is required.[/] Pass it as the first argument, in tap.config, via TAP_UPSTREAM, or save a profile.");
            return 2;
        }

        // Validate the auth flags before anything is provisioned: a misconfigured gate has to stop
        // the run, not start a public tunnel and then discover it has nothing to enforce.
        if (!TryBuildAuthOptions(merged, out var auth, out var authError))
        {
            AnsiConsole.MarkupLine($"[red]✘[/]  [grey]{Markup.Escape(authError!)}[/]");
            return 2;
        }

        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddSpectreConsoleLogger());
        // 1) Resolve tunnel mode + provision (if any) — show one spinner per phase.
        var tunnelMode = merged.ResolveMode();
        ProvisionedTunnel? provisioned = null;
        Process? cloudflaredProcess = null;
        string? quickPublicUrl = null;
        TailscaleCliRunner.FunnelHandle? tailscaleFunnel = null;

        if (tunnelMode == TunnelMode.Tailscale
            && merged.TailscaleDaemonMode == TailscaleDaemonMode.Ephemeral
            && string.IsNullOrWhiteSpace(merged.TailscaleAuthKey))
        {
            AnsiConsole.MarkupLine(
                "[yellow]⚠[/]  [grey]Tailscale ephemeral mode requested but no auth key provided. "
                + "Pass --tailscale-authkey, set TAILSCALE_AUTHKEY, or store one in the profile. "
                + "Falling back to system mode.[/]");
        }

        // --docker without an auth key implies "ephemeral via Docker" but we have nothing to authenticate
        // with. Reject up front rather than silently falling through to system mode.
        if (tunnelMode == TunnelMode.Tailscale
            && merged.TailscaleHostMode == TailscaleHostMode.Docker
            && string.IsNullOrWhiteSpace(merged.TailscaleAuthKey))
        {
            AnsiConsole.MarkupLine(
                "[red]✘[/]  [grey]--tailscale --docker requires an auth key (Docker mode is ephemeral by design). "
                + "Pass --tailscale-authkey, set TAILSCALE_AUTHKEY, or drop --docker to use the host's tailscaled.[/]");
            return 2;
        }

        // Process-ephemeral on Windows isn't supported (no userspace tailscaled spawn path);
        // surface this BEFORE the spinner instead of letting the runner throw later.
        if (tunnelMode == TunnelMode.Tailscale
            && merged.TailscaleDaemonMode == TailscaleDaemonMode.Ephemeral
            && merged.TailscaleHostMode == TailscaleHostMode.Process
            && !string.IsNullOrWhiteSpace(merged.TailscaleAuthKey)
            && OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine(
                "[red]✘[/]  [grey]Tailscale ephemeral process mode is not supported on Windows from the CLI. "
                + "Add --docker to run via the `tailscale/tailscale` container, or drop the auth key for system mode.[/]");
            return 2;
        }

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse(tunnelMode == TunnelMode.Tailscale ? "blue" : "orange3"))
                .StartAsync("Initializing tap...", async ctx =>
                {
                    if (tunnelMode == TunnelMode.None) return;

                    if (tunnelMode == TunnelMode.Tailscale)
                    {
                        // Auth key absence means we fall back to system mode regardless of profile setting.
                        var effectiveDaemonMode = merged.TailscaleDaemonMode == TailscaleDaemonMode.Ephemeral
                            && !string.IsNullOrWhiteSpace(merged.TailscaleAuthKey)
                                ? TailscaleDaemonMode.Ephemeral
                                : TailscaleDaemonMode.System;

                        var statusText = (effectiveDaemonMode, merged.TailscaleHostMode) switch
                        {
                            (TailscaleDaemonMode.Ephemeral, TailscaleHostMode.Docker) => "Starting tailscale/tailscale container...",
                            (TailscaleDaemonMode.Ephemeral, _) => "Spawning ephemeral tailscaled...",
                            _ => "Configuring Tailscale Funnel...",
                        };
                        ctx.Status(statusText);

                        tailscaleFunnel = await TailscaleCliRunner.StartAsync(
                            effectiveDaemonMode,
                            merged.TailscaleHostMode,
                            merged.TailscaleFunnelPort,
                            $"http://localhost:{merged.ProxyPort}",
                            merged.TailscaleAuthKey,
                            merged.TailscaleLoginServer,
                            merged.TailscalePublic,
                            ct);
                        return;
                    }

                    if (merged.HostMode == CloudflaredHostMode.Process)
                    {
                        ctx.Status("Resolving cloudflared binary...");
                        var installer = new CloudflaredInstaller(loggerFactory.CreateLogger<CloudflaredInstaller>());
                        await installer.EnsureAvailableAsync(merged.AutoInstall, ct);
                    }

                    ctx.Status($"Provisioning {tunnelMode.ToString().ToLowerInvariant()} tunnel...");
                    var spec = new TunnelSpec
                    {
                        Mode = tunnelMode,
                        Token = merged.Token,
                        ApiToken = merged.ApiToken,
                        AccountId = merged.AccountId,
                        TunnelName = merged.ApiManagedTunnelName ?? "tap-cli",
                        LocalUrl = $"http://localhost:{merged.ProxyPort}",
                        DynamicZoneName = merged.DynamicZone,
                        DynamicHostnamePrefix = "tap-",
                        DynamicHostnameSuffix = "",
                    };
                    if (!string.IsNullOrWhiteSpace(merged.Hostname)) spec.Hostnames.Add(merged.Hostname);

                    var provisioner = new TunnelProvisioner(loggerFactory.CreateLogger<TunnelProvisioner>());
                    provisioned = await provisioner.ProvisionAsync(spec, ct);
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✘ Setup failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // 2) Start cloudflared (if any). For quick mode, wait for the trycloudflare URL
        //    BEFORE printing the banner so the public URL shows up inside the box.
        if (provisioned is not null && provisioned.Mode != TunnelMode.None)
        {
            var built = await CloudflaredCommand.BuildAsync(new CloudflaredRunSpec
            {
                HostMode = merged.HostMode,
                Tunnel = provisioned,
                InspectorProxyUrl = $"http://localhost:{merged.ProxyPort}",
            }, ct);

            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("orange3"))
                    .StartAsync("Starting cloudflared...", async ctx =>
                    {
                        var psi = new ProcessStartInfo(built.FileName)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };
                        foreach (var a in built.Args) psi.ArgumentList.Add(a);
                        cloudflaredProcess = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Failed to start {built.FileName}.");

                        if (provisioned.Mode == TunnelMode.Quick)
                        {
                            ctx.Status("Waiting for TryCloudflare hostname...");
                            quickPublicUrl = await WaitForTryCloudflareUrlAsync(cloudflaredProcess, TimeSpan.FromSeconds(30), ct);
                        }
                        else
                        {
                            // Drain pipes silently (or to verbose log) so the process doesn't block on a full pipe.
                            _ = DrainAsync(cloudflaredProcess.StandardOutput, merged.Verbose ? "cloudflared" : null);
                            _ = DrainAsync(cloudflaredProcess.StandardError, merged.Verbose ? "cloudflared" : null);
                            ctx.Status("Routing through inspector...");
                            await Task.Delay(500, ct); // small grace period for cloudflared to register
                        }
                    });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✘ cloudflared failed:[/] {Markup.Escape(ex.Message)}");
                if (cloudflaredProcess is { HasExited: false }) cloudflaredProcess.Kill(true);
                return 1;
            }
        }

        // 3) Build the inspector options NOW that we know the public URL.
        var publicUrl = tailscaleFunnel?.PublicUrl
            ?? quickPublicUrl
            ?? (provisioned?.Hostnames.FirstOrDefault() is { Length: > 0 } h ? $"https://{h}" : null);

        // For Tailscale we use "tailscale-system" / "tailscale-ephemeral" as the inspector
        // tunnelMode so the UI picks the right branding and panel.
        var isEphemeral = tailscaleFunnel?.DaemonProcess is not null || tailscaleFunnel?.DockerContainer is not null;
        var inspectorTunnelMode = tunnelMode switch
        {
            TunnelMode.None => null,
            TunnelMode.Tailscale => isEphemeral ? "tailscale-ephemeral" : "tailscale-system",
            _ => tunnelMode.ToString().ToLowerInvariant(),
        };
        var provider = tunnelMode switch
        {
            TunnelMode.None => null,
            TunnelMode.Tailscale => "tailscale",
            _ => "cloudflare",
        };
        var hostname = tailscaleFunnel?.MagicDnsName
            ?? provisioned?.Hostnames.FirstOrDefault()
            ?? string.Empty;

        var publicExpose = tunnelMode switch
        {
            TunnelMode.None => false,
            TunnelMode.Tailscale => tailscaleFunnel?.PublicExpose ?? merged.TailscalePublic,
            _ => true, // any Cloudflare flavor
        };

        var ingress = new[]
        {
            new InspectorIngressEntry(
                Hostname: hostname,
                Upstream: merged.Upstream)
            {
                TunnelMode = inspectorTunnelMode,
                TunnelName = tailscaleFunnel is not null ? "tailscale-funnel" : provisioned?.TunnelName,
                PublicUrl = publicUrl,
                PublicExpose = publicExpose,
            },
        };

        var inspectorOptions = new TapInspectorOptions
        {
            ProxyPort = merged.ProxyPort,
            UiPort = merged.UiPort,
            Ingress = ingress,
            Mode = tunnelMode == TunnelMode.None ? "standalone" : "tunnel",
            Auth = auth,
            Provider = provider,
            TunnelMode = inspectorTunnelMode,
            TunnelName = tailscaleFunnel is not null
                ? "tailscale-funnel"
                : provisioned?.TunnelName ?? merged.ApiManagedTunnelName,
            TunnelResourceName = "cli",
            TunnelPublicUrl = publicUrl,
            TunnelAccountId = provisioned?.AccountId ?? merged.AccountId,
            TunnelTunnelId = provisioned?.TunnelId,
            TunnelApiToken = merged.ApiToken,
            TunnelSocketPath = tailscaleFunnel?.SocketPath, // ephemeral mode: point inspector at our per-session daemon
            Quiet = !merged.Verbose,
        };

        // 4) Print the banner once, fully populated.
        PrintBanner(merged, inspectorOptions, ingress[0]);

        // 5) Start the inspector quietly. We use StartAsync (not RunAsync) so we can
        //    subscribe to its capture stream and stop it ourselves on Ctrl+C / 'q'.
        var app = TapInspectorHost.Build([], inspectorOptions);
        await app.StartAsync(ct);

        // 6) Background key reader: 'q' triggers the same cancellation as Ctrl+C.
        StartKeyWatcher(cts);

        // 7) Live-refreshing table of the last 10 requests.
        try
        {
            await RenderRequestLogAsync(app, ct);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // Spectre.Live can throw spurious exceptions during cancellation cleanup;
            // we don't want that to block the shutdown path.
            AnsiConsole.MarkupLine($"[dim]({Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)})[/]");
        }

        // 8) Graceful shutdown with visible progress and a hard ceiling.
        await ShutdownAsync(app, cloudflaredProcess, tailscaleFunnel);
        return 0;
    }

    private static void StartKeyWatcher(CancellationTokenSource cts)
    {
        if (Console.IsInputRedirected) return; // detached / piped — no key reader

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // Poll instead of blocking on Console.ReadKey: a native syscall
                    // would keep the thread alive past process shutdown.
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(intercept: true);
                        if (k.KeyChar is 'q' or 'Q')
                        {
                            cts.Cancel();
                            return;
                        }
                    }
                    await Task.Delay(80, cts.Token);
                }
            }
            catch (OperationCanceledException) { /* expected during shutdown */ }
            catch (InvalidOperationException) { /* no terminal attached */ }
        });
    }

    private static async Task RenderRequestLogAsync(IHost app, CancellationToken ct)
    {
        var store = app.Services.GetRequiredService<IRequestStore>();
        var ring = new Queue<RequestRecord>();

        AnsiConsole.MarkupLine("[dim]  Requests (last 10) — press [bold]q[/] or [bold]Ctrl+C[/] to stop[/]");
        AnsiConsole.WriteLine();

        // When stdout is not a TTY (CI, log redirect), Spectre's Live becomes a no-op
        // that buffers — and worse, doesn't observe cancellation snappily. Fall back to
        // plain per-request lines.
        if (Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            try
            {
                await foreach (var ev in store.Stream(ct))
                {
                    if (ev is RecordEvent re)
                    {
                        PrintPlainRequestLine(re.Record);
                    }
                }
            }
            catch (OperationCanceledException) { }
            return;
        }

        var table = NewRequestTable();
        // Seed an empty placeholder row so Spectre's Live can render a stable layout
        // even before any traffic arrives.
        table.AddRow("[grey]waiting[/]", "", "", "", "");

        await AnsiConsole.Live(table)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                try
                {
                    await foreach (var ev in store.Stream(ct))
                    {
                        if (ev is not RecordEvent re) continue;
                        ring.Enqueue(re.Record);
                        while (ring.Count > 10) ring.Dequeue();
                        RefillRequestTable(table, ring);
                        ctx.Refresh();
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* ignore Spectre rendering errors during shutdown */ }
            });
    }

    private static void PrintPlainRequestLine(RequestRecord r)
    {
        var statusColor = r.StatusCode >= 500 ? "red"
            : r.StatusCode >= 400 ? "orange3"
            : r.StatusCode >= 300 ? "yellow"
            : r.StatusCode >= 200 ? "green"
            : "grey";
        var time = r.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        var path = r.Path.Length > 50 ? r.Path[..47] + "..." : r.Path.PadRight(50);
        AnsiConsole.MarkupLine(
            $"[grey]{time}[/]  [bold]{Markup.Escape(r.Method.PadRight(4))}[/]  " +
            $"{Markup.Escape(path)}  [{statusColor}]{r.StatusCode}[/]  [dim]{r.DurationMs}ms[/]");
    }

    private static Table NewRequestTable()
    {
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("time").NoWrap())
            .AddColumn(new TableColumn("method").NoWrap())
            .AddColumn(new TableColumn("path"))
            .AddColumn(new TableColumn("status").NoWrap())
            .AddColumn(new TableColumn("ms").NoWrap().RightAligned());
        return table;
    }

    private static void RefillRequestTable(Table table, IEnumerable<RequestRecord> ring)
    {
        table.Rows.Clear();
        foreach (var r in ring)
        {
            var statusColor = r.StatusCode >= 500 ? "red"
                : r.StatusCode >= 400 ? "orange3"
                : r.StatusCode >= 300 ? "yellow"
                : r.StatusCode >= 200 ? "green"
                : "grey";
            var time = r.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var path = r.Path.Length > 60 ? r.Path[..57] + "..." : r.Path;
            table.AddRow(
                $"[grey]{time}[/]",
                $"[bold]{Markup.Escape(r.Method)}[/]",
                $"[white]{Markup.Escape(path)}[/]",
                $"[{statusColor}]{r.StatusCode}[/]",
                $"[dim]{r.DurationMs}ms[/]"
            );
        }
    }

    private static async Task ShutdownAsync(
        IHost app,
        Process? cloudflaredProcess,
        TailscaleCliRunner.FunnelHandle? tailscaleFunnel)
    {
        if (tailscaleFunnel is not null)
        {
            var msg = tailscaleFunnel.DockerContainer is not null
                ? "[grey]· removing Tailscale Docker container...[/]"
                : tailscaleFunnel.DaemonProcess is not null
                    ? "[grey]· stopping ephemeral tailscaled + cleaning up funnel rule...[/]"
                    : "[grey]· removing Tailscale funnel rule...[/]";
            AnsiConsole.MarkupLine(msg);
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await TailscaleCliRunner.StopAsync(tailscaleFunnel, stopCts.Token); } catch { }
        }

        if (cloudflaredProcess is not null)
        {
            AnsiConsole.MarkupLine("[grey]· stopping cloudflared...[/]");
            if (!cloudflaredProcess.HasExited)
            {
                try { cloudflaredProcess.Kill(entireProcessTree: true); } catch { }
                try { await cloudflaredProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }
        }

        AnsiConsole.MarkupLine("[grey]· stopping inspector...[/]");
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await app.StopAsync(stopCts.Token); } catch { }
        }

        AnsiConsole.MarkupLine("[green]✓[/] [dim]stopped.[/]");
    }

    private static async Task<string?> WaitForTryCloudflareUrlAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        var rx = new Regex(@"https?://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Read(StreamReader r)
        {
            string? line;
            while ((line = await r.ReadLineAsync(ct)) is not null)
            {
                var m = rx.Match(line);
                if (m.Success) tcs.TrySetResult(m.Value);
            }
        }
        _ = Read(p.StandardError);
        _ = Read(p.StandardOutput);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static Task DrainAsync(StreamReader r, string? echoLabel) => Task.Run(async () =>
    {
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
        {
            if (echoLabel is not null) AnsiConsole.MarkupLine($"[grey][[{echoLabel}]][/] {Markup.Escape(line)}");
        }
    });

    /// <summary>
    /// Builds the auth gate from the merged flags, or fails with a message. Nothing here is allowed
    /// to produce a blank credential: <c>--auth-header "X-Tap-Key=$TAP_KEY"</c> with an unset
    /// <c>$TAP_KEY</c> would otherwise register a gate with nothing to compare against.
    /// </summary>
    private static bool TryBuildAuthOptions(MergedSettings s, out TapAuthOptions? auth, out string? error)
    {
        auth = null;
        error = null;

        var built = new TapAuthOptions();
        if (!string.IsNullOrWhiteSpace(s.AuthHeader))
        {
            var parts = s.AuthHeader.Split('=', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = "--auth-header must be NAME=VALUE with a non-empty value " +
                    $"(got '{parts[0]}=…'). An empty value would register the auth gate with nothing to check " +
                    "— if $TAP_KEY is unset in your shell, that is what happened.";
                return false;
            }
            built.Header = new HeaderAuthOptions { Name = parts[0], Value = parts[1] };
        }
        if (s.AuthCidrs is { Length: > 0 }) built.AllowedCidrs = [.. s.AuthCidrs];
        if (s.AuthCountries is { Length: > 0 }) built.AllowedCountries = [.. s.AuthCountries.Select(c => c.ToUpperInvariant())];
        if (!string.IsNullOrWhiteSpace(s.OidcAuthority) && !string.IsNullOrWhiteSpace(s.OidcClientId))
        {
            built.Oidc = new OidcAuthOptions
            {
                Authority = s.OidcAuthority,
                ClientId = s.OidcClientId,
                ClientSecret = s.OidcClientSecret,
            };
        }

        auth = built.AnyConfigured ? built : null;
        return true;
    }

    private static void PrintBanner(MergedSettings s, TapInspectorOptions opts, InspectorIngressEntry ingress)
    {
        var grid = new Grid().AddColumn().AddColumn();
        void Row(string k, string v) => grid.AddRow($"[grey]{k}[/]", v);

        if (!string.IsNullOrEmpty(ingress.PublicUrl))
            Row("Public URL", $"[orange3 bold][link={ingress.PublicUrl}]{Markup.Escape(ingress.PublicUrl)}[/][/]");
        Row("Upstream", $"[link]{Markup.Escape(opts.Ingress[0].Upstream)}[/]");
        Row("Inspector UI", $"[link]http://localhost:{opts.UiPort}[/]");
        Row("Inspector proxy", $"http://localhost:{opts.ProxyPort}");
        // Deep link to the inspector's QR page — the easiest way to open this URL on a phone.
        // Most modern terminals (iTerm, kitty, Wezterm, recent macOS Terminal) make this clickable.
        if (!string.IsNullOrEmpty(ingress.PublicUrl))
            Row("Open on phone", $"[link=http://localhost:{opts.UiPort}/#qr]http://localhost:{opts.UiPort}/#qr[/]  [grey](QR code)[/]");
        if (!string.IsNullOrEmpty(opts.TunnelMode)) Row("Tunnel", opts.TunnelMode!);
        if (opts.Auth is { } auth)
        {
            // Only the checks that will actually execute. Deriving this from non-nullness is how a
            // blank credential ended up being announced as enforced auth.
            var bits = new List<string>();
            if (auth.HeaderConfigured) bits.Add($"header({auth.Header!.Name})");
            if (auth.JwtConfigured) bits.Add("jwt");
            if (auth.AllowedCidrs is { Count: > 0 }) bits.Add($"cidr×{auth.AllowedCidrs.Count}");
            if (auth.AllowedCountries is { Count: > 0 }) bits.Add($"country×{auth.AllowedCountries.Count}");
            if (auth.Oidc is not null) bits.Add("oidc");
            Row("Auth", bits.Count > 0 ? string.Join(" + ", bits) : "[red]none enforced[/]");
        }
        var panel = new Panel(grid)
            .Header("[bold] tap [/]", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Orange3)
            .Padding(1, 0);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public static MergedSettings MergeWithEnvAndConfig(TapBaseSettings s, string? upstream, TunnelProfile? profile = null)
    {
        // Priority (highest first): explicit CLI flag, env var, profile, tap.config file, default.
        var fromConfig = LoadConfigFile(s.ConfigPath);
        string? Pick(string? cli, string? env, string? profileVal, string? cfg)
            => !string.IsNullOrEmpty(cli) ? cli
                : !string.IsNullOrEmpty(env) ? env
                : !string.IsNullOrEmpty(profileVal) ? profileVal
                : cfg;

        return new MergedSettings
        {
            Upstream = Pick(upstream, Environment.GetEnvironmentVariable("TAP_UPSTREAM"), profile?.Upstream, fromConfig?.GetValueOrDefault("upstream")),
            ProxyPort = NonDefault(s.ProxyPort, 4444) ?? profile?.ProxyPort ?? 4444,
            UiPort = NonDefault(s.UiPort, 4445) ?? profile?.UiPort ?? 4445,
            Quick = s.Quick || profile?.TunnelMode == TunnelMode.Quick,
            Token = Pick(s.Token, Environment.GetEnvironmentVariable("CLOUDFLARE_TUNNEL_TOKEN"), profile?.Token, null),
            ApiToken = Pick(s.ApiToken, Environment.GetEnvironmentVariable("CLOUDFLARE_API_TOKEN"), profile?.ApiToken, null),
            AccountId = Pick(s.AccountId, Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID"), profile?.AccountId, null),
            ApiManagedTunnelName = s.ApiManagedTunnelName ?? profile?.ApiManagedTunnelName,
            DynamicZone = s.DynamicZone ?? profile?.DynamicZone,
            Hostname = s.Hostname ?? profile?.Hostname,
            HostMode = (s.Docker || (profile?.Docker ?? false)) ? CloudflaredHostMode.Docker : CloudflaredHostMode.Process,
            AutoInstall = s.AutoInstall || (profile?.AutoInstall ?? false),
            Tailscale = s.Tailscale || profile?.TunnelMode == TunnelMode.Tailscale,
            // Profile.TailscaleDaemonMode is the explicit setting. Auth-key presence (CLI flag, env,
            // or profile) implicitly opts into ephemeral mode if no explicit mode is set.
            TailscaleAuthKey = Pick(s.TailscaleAuthKey, Environment.GetEnvironmentVariable("TAILSCALE_AUTHKEY"), profile?.TailscaleAuthKey, null),
            TailscaleLoginServer = Pick(s.TailscaleLoginServer, Environment.GetEnvironmentVariable("TAILSCALE_LOGIN_SERVER"), profile?.TailscaleLoginServer, null),
            TailscaleDaemonMode = ResolveTailscaleDaemonMode(s, profile),
            // Shared --docker flag: applies to whichever provider is active.
            TailscaleHostMode = (s.Docker || (profile?.Docker ?? false)) ? TailscaleHostMode.Docker : TailscaleHostMode.Process,
            TailscaleFunnelPort = ValidateFunnelPort(s.TailscaleFunnelPort ?? profile?.TailscaleFunnelPort ?? 443),
            TailscalePublic = s.TailscalePublic || (profile?.TailscalePublic ?? false),
            AuthHeader = s.AuthHeader ?? profile?.AuthHeader,
            AuthCidrs = s.AuthCidrs ?? profile?.AuthCidrs,
            AuthCountries = s.AuthCountries ?? profile?.AuthCountries,
            OidcAuthority = s.OidcAuthority ?? profile?.OidcAuthority,
            OidcClientId = s.OidcClientId ?? profile?.OidcClientId,
            OidcClientSecret = s.OidcClientSecret ?? profile?.OidcClientSecret,
            Verbose = s.Verbose,
        };
    }

    private static int? NonDefault(int value, int defaultValue) => value == defaultValue ? null : value;

    private static int ValidateFunnelPort(int port)
    {
        if (port is 443 or 8443 or 10000) return port;
        throw new ArgumentException(
            $"--tailscale-port must be 443, 8443, or 10000 (got {port}).",
            nameof(port));
    }

    private static TailscaleDaemonMode ResolveTailscaleDaemonMode(TapBaseSettings s, TunnelProfile? profile)
    {
        // 1. CLI flag wins outright — explicit force-system overrides everything else.
        if (s.TailscaleSystem) return TailscaleDaemonMode.System;
        // 2. Profile's explicit setting wins next.
        if (profile?.TailscaleDaemonMode is { } explicitMode) return explicitMode;
        // 3. Otherwise, presence of an auth key (CLI flag, env, or profile field) implies ephemeral.
        var authKey = !string.IsNullOrWhiteSpace(s.TailscaleAuthKey) ? s.TailscaleAuthKey
            : Environment.GetEnvironmentVariable("TAILSCALE_AUTHKEY") ?? profile?.TailscaleAuthKey;
        return string.IsNullOrWhiteSpace(authKey) ? TailscaleDaemonMode.System : TailscaleDaemonMode.Ephemeral;
    }

    private static int ListProfiles(TunnelProfileStore store)
    {
        var profiles = store.ListAll();
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No saved tunnel profiles.[/]");
            AnsiConsole.MarkupLine($"[grey]Save one with:[/] tap save <NAME> <upstream> [[opts]]");
            AnsiConsole.MarkupLine($"[grey]Profiles dir:[/] {Markup.Escape(store.RootDirectory)}");
            return 0;
        }

        var table = new Table()
            .Title($"[bold]Saved tunnels[/]  [grey]({store.RootDirectory})[/]")
            .BorderColor(Color.Grey).Border(TableBorder.Rounded);
        table.AddColumns("Name", "Upstream", "Mode", "Hostname / Zone", "Auth");
        foreach (var p in profiles)
        {
            var modeLabel = p.TunnelMode switch
            {
                TunnelMode.None => "[grey]standalone[/]",
                TunnelMode.Quick => "[orange3]quick[/]",
                TunnelMode.Token => "[orange3]token[/]",
                TunnelMode.ApiManaged => "[orange3]api-managed[/]",
                TunnelMode.Dynamic => "[orange3]dynamic[/]",
                TunnelMode.Tailscale => "[blue]tailscale[/]",
                _ => p.TunnelMode.ToString().ToLowerInvariant(),
            };
            var host = p.TunnelMode switch
            {
                TunnelMode.Dynamic => p.DynamicZone ?? "—",
                TunnelMode.Tailscale => "<machine>.<tailnet>.ts.net",
                _ => p.Hostname ?? "—",
            };
            var auth = new List<string>();
            if (!string.IsNullOrEmpty(p.AuthHeader)) auth.Add("header");
            if (p.AuthCidrs is { Length: > 0 }) auth.Add($"cidr×{p.AuthCidrs.Length}");
            if (p.AuthCountries is { Length: > 0 }) auth.Add($"country×{p.AuthCountries.Length}");
            if (!string.IsNullOrEmpty(p.OidcAuthority)) auth.Add("oidc");
            table.AddRow(
                $"[bold]{Markup.Escape(p.Name)}[/]",
                Markup.Escape(p.Upstream ?? "—"),
                modeLabel,
                Markup.Escape(host),
                auth.Count == 0 ? "[grey]—[/]" : string.Join(" + ", auth));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Run one with:[/] tap run --name <NAME>");
        return 0;
    }

    private static Dictionary<string, string?>? LoadConfigFile(string? path)
    {
        path ??= File.Exists("tap.config") ? "tap.config" : null;
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString());
        }
        catch
        {
            return null;
        }
    }

    public sealed class MergedSettings
    {
        public string? Name { get; init; }
        public string? Upstream { get; init; }
        public int ProxyPort { get; init; }
        public int UiPort { get; init; }
        public bool Quick { get; init; }
        public string? Token { get; init; }
        public string? ApiToken { get; init; }
        public string? AccountId { get; init; }
        public string? ApiManagedTunnelName { get; init; }
        public string? DynamicZone { get; init; }
        public string? Hostname { get; init; }
        public CloudflaredHostMode HostMode { get; init; }
        public bool AutoInstall { get; init; }
        // Tailscale Funnel fields (used when ProfileTunnelMode == Tailscale).
        public bool Tailscale { get; init; }
        public TailscaleDaemonMode TailscaleDaemonMode { get; init; } = TailscaleDaemonMode.System;
        public TailscaleHostMode TailscaleHostMode { get; init; } = TailscaleHostMode.Process;
        public string? TailscaleAuthKey { get; init; }
        public string? TailscaleLoginServer { get; init; }
        public int TailscaleFunnelPort { get; init; } = 443;
        /// <summary>True = public funnel; false = tailnet-only serve (default).</summary>
        public bool TailscalePublic { get; init; }
        public string? AuthHeader { get; init; }
        public string[]? AuthCidrs { get; init; }
        public string[]? AuthCountries { get; init; }
        public string? OidcAuthority { get; init; }
        public string? OidcClientId { get; init; }
        public string? OidcClientSecret { get; init; }
        public bool Verbose { get; init; }

        public TunnelMode ResolveMode()
        {
            if (Tailscale) return TunnelMode.Tailscale;
            if (Quick) return TunnelMode.Quick;
            if (!string.IsNullOrWhiteSpace(Token)) return TunnelMode.Token;
            if (!string.IsNullOrWhiteSpace(DynamicZone)) return TunnelMode.Dynamic;
            if (!string.IsNullOrWhiteSpace(ApiManagedTunnelName)) return TunnelMode.ApiManaged;
            return TunnelMode.None;
        }
    }
}
