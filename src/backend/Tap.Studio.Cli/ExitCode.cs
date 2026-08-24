namespace Tap.Studio.Cli;

/// <summary>
/// The exit-code contract, and part of the CLI's public interface — a pipeline branches on
/// these, so changing one is a breaking change.
///
/// <para>The distinction that matters is <see cref="TestFailed"/> versus everything above it:
/// a red build because an API misbehaved is a completely different situation from a red build
/// because the runner could not do its job, and a tool that returns 1 for both makes that
/// impossible to tell apart from a CI dashboard.</para>
/// </summary>
public static class ExitCode
{
    /// <summary>Everything that ran, passed.</summary>
    public const int Ok = 0;

    /// <summary>At least one test or assertion failed. The run itself was fine.</summary>
    public const int TestFailed = 1;

    /// <summary>Usage error — unknown or ambiguous name, bad option, malformed <c>--var</c>.</summary>
    public const int UsageError = 2;

    /// <summary>Workspace error — no <c>workspace.tap</c>, a file that doesn't parse, a dangling ref.</summary>
    public const int WorkspaceError = 3;

    /// <summary>An auth profile needed a token that couldn't be acquired without a human.</summary>
    public const int AuthUnavailable = 4;

    /// <summary>Cancelled — Ctrl-C, or a pipeline timeout. The conventional 128 + SIGINT.</summary>
    public const int Cancelled = 130;
}
