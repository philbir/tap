using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tap.Studio.Ai;

namespace Tap.Studio.Variables;

/// <summary>
/// Shared access to the local 1Password CLI (<c>op</c>). Owns binary location, version
/// detection, vault listing for the picker dialog, and the error shaping every caller wants —
/// so <see cref="OnePasswordVariableProvider"/> and the <c>/api/onepassword/*</c> endpoints
/// spawn the CLI exactly one way.
///
/// <para>Nothing here caches: the provider owns its own listing cache, and the picker/detect
/// endpoints are explicit user actions where a stale answer would be worse than a spawn.</para>
/// </summary>
public static class OnePasswordCli
{
    /// <summary>Env var pointing at the <c>op</c> binary, mirroring <c>TAP_CLAUDE_CLI</c> /
    /// <c>TAP_COPILOT_CLI</c> for the AI providers.</summary>
    public const string EnvOverride = "TAP_OP_CLI";

    /// <summary>CLI required for <c>op environment read</c>. 1Password Environments are still
    /// beta, so this exists on the beta channel only — a stable build (2.38.1 included)
    /// answers <c>unknown command "environment"</c>. The command first appeared in
    /// 2.33.0-beta.02; we quote the build Tap is verified against rather than the earliest
    /// one that technically has it.</summary>
    public const string EnvironmentsMinVersion = "2.38.2-beta.01";

    /// <summary>Where to get the CLI, and where to get the beta channel specifically.</summary>
    public const string DownloadUrl = "https://www.1password.dev/cli";
    public const string BetaDownloadUrl = "https://app-updates.agilebits.com/product_history/CLI2";

    public static LocatedCli Locate(string? overridePath) => CliLocator.Locate(
        "op", Candidates(), Environment.GetEnvironmentVariable(EnvOverride), overridePath);

    private static string[] Candidates() => CliLocator.IsWindows
        ?
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1Password CLI", "op.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "op.exe"),
        ]
        :
        [
            "/opt/homebrew/bin/op",
            "/usr/local/bin/op",
            "/usr/bin/op",
        ];

    /// <summary>Locate + probe, shaped like <c>CopilotCliProvider.DetectAsync</c> so the
    /// settings form's "Detect" button behaves the same for every CLI Tap shells out to.
    /// <c>op --version</c> prints a bare version ("2.38.1"), so no line matcher is needed.</summary>
    public static async Task<CliDetection> DetectAsync(string? overridePath, CancellationToken ct)
    {
        var cli = Locate(overridePath);
        if (!cli.Found)
        {
            return new CliDetection(false, cli.Source == CliSource.Fallback ? null : cli.Command, cli.Source, null,
                cli.Rejection ?? (cli.Source == CliSource.Fallback
                    ? "Could not find the 1Password CLI ('op') in common install paths or on PATH. " +
                      "Install it with `brew install 1password-cli` or `winget install AgileBits.1Password.CLI`."
                    : $"1Password CLI path \"{cli.Command}\" does not exist or is not executable."));
        }
        try
        {
            var version = await CliLocator.GetVersionAsync(cli.Command, lineMatch: null, ct).ConfigureAwait(false);
            return new CliDetection(true, cli.Command, cli.Source, version, null);
        }
        catch (Exception e)
        {
            return new CliDetection(false, cli.Command, cli.Source, null, e.Message);
        }
    }

    /// <summary>Spawns <c>op</c> with the ambient auth the host already has. A configured
    /// service-account token is layered onto the child's environment only — never written to
    /// disk, never logged.</summary>
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        LocatedCli cli,
        IReadOnlyList<string> args,
        string? account,
        string? serviceAccountToken,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!cli.Found)
        {
            throw new OnePasswordCliException(cli.Rejection ??
                "the 1Password CLI ('op') was not found. Install it (brew install 1password-cli / " +
                "winget install AgileBits.1Password.CLI), or set the provider's 'cliPath' setting.");
        }

        var argv = new List<string>(args);
        if (!string.IsNullOrWhiteSpace(account))
        {
            argv.Add("--account");
            argv.Add(account);
        }

        var env = string.IsNullOrWhiteSpace(serviceAccountToken)
            ? null
            : new Dictionary<string, string> { ["OP_SERVICE_ACCOUNT_TOKEN"] = serviceAccountToken };

        return await CliLocator.RunAsync(cli.Command, argv, stdin: null, timeout, ct, env).ConfigureAwait(false);
    }

    /// <summary>Vaults visible to the current sign-in — the data behind the vault picker.
    /// Read-only metadata: names, IDs, and item counts, never item contents.</summary>
    public static async Task<IReadOnlyList<OnePasswordVault>> ListVaultsAsync(
        string? cliPath, string? account, string? serviceAccountToken, CancellationToken ct)
    {
        var cli = Locate(cliPath);
        var (code, stdout, stderr) = await RunAsync(
            cli, ["vault", "list", "--format", "json"], account, serviceAccountToken,
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        if (code != 0) throw new OnePasswordCliException(Explain(code, stderr, stdout));
        return Deserialize(stdout, OnePasswordJson.Default.OnePasswordVaultArray) ?? [];
    }

    /// <summary>True when <c>op</c> said the item isn't there. Matched narrowly on purpose:
    /// the sibling "isn't a vault in this account" and "more than one item matches" messages
    /// must stay hard failures rather than degrading into a silent empty result.</summary>
    public static bool IsItemMiss(string stderr) =>
        stderr.Contains("isn't an item", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no item matches", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no items matched", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("item not found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("couldn't find an item", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this build of <c>op</c> has never heard of <paramref name="command"/>
    /// — the signature of a CLI too old for a beta-gated feature.</summary>
    public static bool IsUnknownCommand(string stderr, string command) =>
        stderr.Contains($"unknown command \"{command}\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns a non-zero <c>op</c> exit into a message worth showing. The timeout case
    /// gets its own wording because the raw "Timed out after 30s" hides the overwhelmingly
    /// common cause: 1Password is waiting on a desktop approval nobody is looking at, or the
    /// CLI session has lapsed. Both are fixed by unlocking once in a terminal.</summary>
    public static string Explain(int code, string stderr, string stdout)
    {
        if (code == -1 && stderr.Contains("Timed out after", StringComparison.Ordinal))
        {
            return "the 'op' CLI didn't respond in time. 1Password may be waiting for you to approve " +
                   "access — check the 1Password app — or its CLI session has expired. Run " +
                   "`op vault list` once in a terminal to unlock it, then retry.";
        }
        return Explain(stderr, stdout);
    }

    /// <summary>Strips <c>op</c>'s "[ERROR] 2006/01/02 15:04:05 " stderr stamp. Inside a
    /// provider-scoped error the severity and the timestamp are both noise.</summary>
    public static string Explain(string stderr, string stdout)
    {
        var message = stderr.Trim() is { Length: > 0 } e ? e : stdout.Trim();
        if (message.Length == 0) return "the 'op' CLI failed without a message.";

        if (message.StartsWith("[ERROR]", StringComparison.Ordinal))
        {
            var newline = message.IndexOf('\n');
            if (newline > 0) message = message[..newline];

            message = message["[ERROR]".Length..].TrimStart();
            var parts = message.Split(' ', 3);
            if (parts.Length == 3
                && parts[0].Count(c => c == '/') == 2
                && parts[1].Count(c => c == ':') == 2)
            {
                message = parts[2];
            }
        }
        return message;
    }

    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new OnePasswordCliException($"could not parse 'op' output as JSON: {ex.Message}");
        }
    }
}

/// <summary>A CLI-level failure carrying a message already fit to show a user. Endpoints turn
/// it into a 400 the picker dialog renders verbatim, mirroring
/// <c>AzureDiscoveryService.AzureDiscoveryException</c>.</summary>
public sealed class OnePasswordCliException(string message) : Exception(message);

// ---- op JSON ----------------------------------------------------------------------------

public sealed record OnePasswordVault(string? Id, string? Name, int Items);

internal sealed record OnePasswordItem(string? Id, string? Title, OnePasswordField[]? Fields);

internal sealed record OnePasswordField(
    string? Id,
    string? Type,
    string? Purpose,
    string? Label,
    string? Value,
    OnePasswordSection? Section);

internal sealed record OnePasswordSection(string? Id, string? Label);

[JsonSerializable(typeof(OnePasswordItem))]
[JsonSerializable(typeof(OnePasswordItem[]))]
[JsonSerializable(typeof(OnePasswordVault[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
internal partial class OnePasswordJson : JsonSerializerContext;
