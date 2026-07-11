using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Core.Cloudflared;

namespace Tap.Cli.Commands;

public sealed class InstallCloudflaredCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSpectreConsoleLogger());
        var installer = new CloudflaredInstaller(loggerFactory.CreateLogger<CloudflaredInstaller>());
        try
        {
            var result = await installer.EnsureAvailableAsync(autoInstall: true, CancellationToken.None);
            AnsiConsole.MarkupLine(result switch
            {
                CloudflaredAvailability.OnPath => "[green]cloudflared is already on PATH.[/]",
                CloudflaredAvailability.Installed => "[green]cloudflared installed via host package manager.[/]",
                _ => "[red]cloudflared could not be installed.[/]",
            });
            return result == CloudflaredAvailability.NotInstalled ? 1 : 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
