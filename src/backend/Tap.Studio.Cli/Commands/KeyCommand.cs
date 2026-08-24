using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tap.Studio.Cli.Output;
using Tap.Workspace.Security;

namespace Tap.Studio.Cli.Commands;

/// <summary>
/// <c>tap-studio key status</c> / <c>key init</c> — this machine's encryption key, the one
/// secret that unlocks everything Tap encrypts at rest (today, <c>file</c>-provider secrets).
///
/// <para>Neither subcommand ever prints the passphrase. <c>status</c> answers "can this
/// machine decrypt?" and says where the key came from; <c>init</c> generates one into
/// <c>&lt;system-dir&gt;/encryption.key</c> and refuses to overwrite an existing key, because
/// the old key is the only thing that can read data written with it.</para>
///
/// <para><c>init</c> is not a prerequisite: storing a secret on a machine with no key creates
/// one first (<see cref="MachineEncryptionKeySource.EnsurePassphrase"/>). It stays for people
/// who want the key provisioned and backed up before there is anything to lose, and for
/// <c>--force</c> rotation.</para>
///
/// <para>CI doesn't use <c>init</c> — it exports <c>TAP_ENCRYPTION_KEY</c>, which wins over
/// the file. That is also why <c>init</c> declines while the variable is set: writing a key
/// nothing reads is worse than doing nothing.</para>
/// </summary>
public sealed class KeyStatusCommand : Command<KeyStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor);
        var source = MachineEncryptionKeySource.Default;

        switch (source.Origin)
        {
            case EncryptionKeyOrigin.Environment:
                console.MarkupLine($"[green]✔[/] Encryption key set via [bold]{MachineEncryptionKeySource.EnvVar}[/].");
                console.MarkupLine($"  [dim]The environment wins over {Markup.Escape(source.KeyFilePath)}.[/]");
                return ExitCode.Ok;

            case EncryptionKeyOrigin.KeyFile:
                console.MarkupLine($"[green]✔[/] Encryption key read from [bold]{Markup.Escape(source.KeyFilePath)}[/].");
                return ExitCode.Ok;

            default:
                console.MarkupLine("[yellow]No encryption key on this machine yet.[/]");
                console.MarkupLine($"  One is generated at [dim]{Markup.Escape(source.KeyFilePath)}[/] the first time a secret is stored,");
                console.MarkupLine($"  so nothing is blocked. Run [bold]tap-studio key init[/] to create it now, or set "
                    + $"[bold]{MachineEncryptionKeySource.EnvVar}[/] to supply your own.");
                // Still non-zero: this machine cannot decrypt anything today, which is what a
                // script gating on `key status` is asking about.
                return ExitCode.WorkspaceError;
        }
    }
}

public sealed class KeyInitCommand : Command<KeyInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--force")]
        [Description("Replace an existing key file. Everything encrypted with the old key becomes unreadable — move the old file aside first if you might still need it.")]
        public bool Force { get; init; }

        [CommandOption("--no-color")]
        [Description("Disable colour.")]
        public bool NoColor { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var console = ConsoleFactory.Create(settings.NoColor);
        var source = MachineEncryptionKeySource.Default;

        if (source.Origin is EncryptionKeyOrigin.Environment && !settings.Force)
        {
            console.MarkupLine($"[yellow]{MachineEncryptionKeySource.EnvVar} is set, and it wins over the key file.[/]");
            console.MarkupLine("  Generating one now would write a key nothing reads. Unset the variable first, or pass --force.");
            return ExitCode.UsageError;
        }

        try
        {
            source.GenerateKeyFile(settings.Force);
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return ExitCode.UsageError;
        }

        console.MarkupLine($"[green]✔[/] Wrote a new encryption key to [bold]{Markup.Escape(source.KeyFilePath)}[/] [dim](owner-only)[/].");
        console.WriteLine();
        console.MarkupLine("[yellow]Back this file up.[/] It is the only thing that can decrypt secrets written with it —");
        console.MarkupLine("losing it loses them. To share the key with CI, export its contents as "
            + $"[bold]{MachineEncryptionKeySource.EnvVar}[/] there.");
        return ExitCode.Ok;
    }
}
