using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Core.Profiles;

namespace Tap.Cli.Commands;

public sealed class RmCommand : Command<RmCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Profile name to delete.")]
        public string ProfileName { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var store = new TunnelProfileStore();
        if (store.Delete(settings.ProfileName))
        {
            AnsiConsole.MarkupLine($"[green]Deleted[/] [bold]{Markup.Escape(settings.ProfileName)}[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"[yellow]No profile named '{Markup.Escape(settings.ProfileName)}'.[/]");
        return 1;
    }
}
