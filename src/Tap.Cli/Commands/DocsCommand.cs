using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tap.Cli.Commands;

public sealed class DocsCommand : Command<DocsCommand.Settings>
{
    public const string Url = "https://philbir.github.io/tap/#cli";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--print")]
        [Description("Print the docs URL instead of opening it in the browser.")]
        public bool Print { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings.Print)
        {
            AnsiConsole.WriteLine(Url);
            return 0;
        }

        AnsiConsole.MarkupLine($"[grey]Opening[/] [link]{Url}[/]");
        if (!BrowserLauncher.TryOpen(Url))
        {
            AnsiConsole.MarkupLine($"[yellow]Could not open a browser. Visit:[/] {Url}");
            return 1;
        }
        return 0;
    }
}
