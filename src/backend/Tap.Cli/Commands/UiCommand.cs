using System.ComponentModel;
using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Server;

namespace Tap.Cli.Commands;

/// <summary>
/// Starts the inspector UI without a tunnel or upstream. Intended for managing
/// Cloudflare tunnel profiles via the bundled web UI.
/// </summary>
public sealed class UiCommand : AsyncCommand<UiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--ui-port")]
        [Description("Inspector UI port (default 4445).")]
        public int UiPort { get; init; } = 4445;

        [CommandOption("--proxy-port")]
        [Description("Inspector proxy port (default 4444). Bound but unused without ingress.")]
        public int ProxyPort { get; init; } = 4444;

        [CommandOption("--no-open")]
        [Description("Don't try to open a browser; just print the URL and serve.")]
        public bool NoOpen { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

        var inspectorOptions = new TapInspectorOptions
        {
            ProxyPort = settings.ProxyPort,
            UiPort = settings.UiPort,
            Ingress = [],
            Mode = "standalone",
            Quiet = true,
        };

        var app = TapInspectorHost.Build([], inspectorOptions);
        await app.StartAsync(ct);

        var url = $"http://localhost:{settings.UiPort}/#/cloudflare";
        var panel = new Panel(new Markup(
            $"[grey]Inspector UI[/]   [link]{url}[/]\n" +
            $"[grey]Profiles dir[/]   {Markup.Escape(new Tap.Core.Profiles.TunnelProfileStore().RootDirectory)}\n\n" +
            "[dim]Press Ctrl+C to stop.[/]"))
            .Header("[bold] tap ui [/]", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Orange3)
            .Padding(1, 0);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        if (!settings.NoOpen)
        {
            BrowserLauncher.TryOpen(url);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }

        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await app.StopAsync(stopCts.Token); } catch { }
        }
        return 0;
    }
}
