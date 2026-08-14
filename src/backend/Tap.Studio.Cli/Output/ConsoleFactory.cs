using Spectre.Console;

namespace Tap.Studio.Cli.Output;

/// <summary>
/// Builds the console the CLI writes to, with colour decided by where the output is going.
///
/// <para>Three signals, any of which turns colour off: an explicit <c>--no-color</c>, the
/// <c>NO_COLOR</c> convention, and a redirected stdout. The last one is what matters most in
/// practice — a CI job captures stdout to a file, and escape codes in a build log are the
/// difference between a readable failure and a wall of noise.</para>
///
/// <para><c>CI=true</c> deliberately does <em>not</em> disable colour: GitHub Actions and
/// GitLab both render ANSI in their log viewers, and a red FAIL is worth having there.</para>
/// </summary>
public static class ConsoleFactory
{
    public static IAnsiConsole Create(bool noColor)
    {
        var suppress = noColor
            || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }
            || Console.IsOutputRedirected;

        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = suppress ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = suppress ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            // Interactivity gates prompts and animations. There is nothing to prompt for, and
            // a spinner redrawing itself a thousand times is exactly what a log doesn't need.
            Interactive = InteractionSupport.No,
        });
    }

    /// <summary>True when this looks like an automated environment. Used only to soften
    /// wording — behaviour must not depend on it, or a run stops being reproducible by hand.</summary>
    public static bool IsCI =>
        Environment.GetEnvironmentVariable("CI") is { Length: > 0 } and not "false" and not "0"
        || Environment.GetEnvironmentVariable("TF_BUILD") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is { Length: > 0 };
}
