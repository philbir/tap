using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tap.Execution.Variables;

/// <summary>How a CLI path was resolved — surfaced to the UI so users can tell a configured
/// override apart from a lucky PATH hit.</summary>
public enum CliSource { Override, Env, Candidate, Where, Path, Fallback }

/// <summary>A located CLI binary plus whether it actually exists/executes.</summary>
public sealed record LocatedCli(string Command, bool Found, CliSource Source)
{
    /// <summary>Set when an explicit override was refused before it was ever probed — it named
    /// something other than the expected binary. Callers surface it instead of the generic
    /// "does not exist" message, which would send the user looking for the wrong problem.</summary>
    public string? Rejection { get; init; }
}

/// <summary>Result of probing a CLI: ok + version, or an actionable error.</summary>
public sealed record CliDetection(bool Ok, string? Path, CliSource Source, string? Version, string? Error);

/// <summary>
/// Shared logic for finding a locally-installed CLI (Copilot / Claude Code), mirroring
/// mango's <c>locateCli</c>: explicit override → env var → well-known per-user install
/// paths → <c>where</c>/<c>command -v</c> on a login shell → bare PATH scan → fallback to
/// the bare name. Also hosts the small process-spawning helpers the providers share.
/// </summary>
public static class CliLocator
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsExecutableFile(string candidate)
    {
        try
        {
            if (!File.Exists(candidate)) return false;
            if (OperatingSystem.IsWindows()) return true; // Windows has no X bit; existence is enough.
            var mode = File.GetUnixFileMode(candidate);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="binaryName">Base CLI name, e.g. <c>copilot</c> or <c>claude</c>.</param>
    /// <param name="candidates">Well-known absolute install paths to probe, in priority order.</param>
    /// <param name="envValue">Value of the provider's env override (already read by the caller).</param>
    /// <param name="overridePath">Explicit path the user configured in settings.</param>
    public static LocatedCli Locate(string binaryName, IReadOnlyList<string> candidates, string? envValue, string? overridePath)
    {
        var cleanedOverride = overridePath?.Trim();
        if (!string.IsNullOrEmpty(cleanedOverride))
        {
            // An override reaches us over HTTP (AI settings, provider settings) and can even
            // come out of a cloned workspace file, so it decides which binary this machine
            // runs. Anything that isn't an absolute path to a file actually named after the
            // expected CLI is refused outright rather than probed and spawned.
            if (RejectOverride(binaryName, cleanedOverride) is { } rejection)
                return new LocatedCli(cleanedOverride, false, CliSource.Override) { Rejection = rejection };
            return new LocatedCli(cleanedOverride, IsExecutableFile(cleanedOverride), CliSource.Override);
        }

        var cleanedEnv = envValue?.Trim();
        if (!string.IsNullOrEmpty(cleanedEnv))
            return new LocatedCli(cleanedEnv, IsExecutableFile(cleanedEnv), CliSource.Env);

        foreach (var c in candidates)
            if (IsExecutableFile(c)) return new LocatedCli(c, true, CliSource.Candidate);

        if (FindWithWhere(binaryName) is { } whereHit)
            return new LocatedCli(whereHit, true, CliSource.Where);

        if (FindOnPath(binaryName) is { } pathHit)
            return new LocatedCli(pathHit, true, CliSource.Path);

        return new LocatedCli(binaryName, false, CliSource.Fallback);
    }

    /// <summary>Null when <paramref name="path"/> is an acceptable override for
    /// <paramref name="binaryName"/>, otherwise the reason to show the user. The rule is
    /// deliberately blunt — absolute path, and the file name is the CLI's own (plus
    /// <c>.exe</c>/<c>.cmd</c> on Windows) — because the alternative is letting a settings
    /// field or a workspace file choose an arbitrary executable.</summary>
    private static string? RejectOverride(string binaryName, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return $"The {binaryName} CLI path must be an absolute path — \"{path}\" is relative.";

        var fileName = Path.GetFileName(path);
        if (!IsExpectedFileName(binaryName, fileName))
        {
            var accepted = IsWindows ? $"{binaryName}, {binaryName}.exe or {binaryName}.cmd" : binaryName;
            return $"The {binaryName} CLI path must point at a file named {accepted} — \"{fileName}\" is not one.";
        }

        return null;
    }

    private static bool IsExpectedFileName(string binaryName, string fileName)
    {
        var comparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fileName, binaryName, comparison)) return true;
        return IsWindows
            && (string.Equals(fileName, binaryName + ".exe", comparison)
                || string.Equals(fileName, binaryName + ".cmd", comparison));
    }

    private static string? FindOnPath(string binaryName)
    {
        var names = IsWindows ? new[] { binaryName + ".cmd", binaryName + ".exe", binaryName } : new[] { binaryName };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (IsExecutableFile(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? FindWithWhere(string binaryName)
    {
        var attempts = IsWindows
            ? new (string, string[])[] { ("where", new[] { binaryName }) }
            : new (string, string[])[]
            {
                ("zsh", new[] { "-lc", $"where {binaryName}" }),
                ("sh", new[] { "-lc", $"command -v {binaryName}" }),
            };

        foreach (var (command, args) in attempts)
        {
            try
            {
                var psi = NewStartInfo(command, args);
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);
                if (proc.ExitCode != 0) continue;
                var candidate = stdout
                    .Split('\n', '\r')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && IsExecutableFile(l));
                if (candidate is not null) return candidate;
            }
            catch
            {
                // Shell not present / blocked — fall through to the next attempt.
            }
        }
        return null;
    }

    /// <summary>Run the CLI's <c>--version</c> and return the first matching line.</summary>
    public static async Task<string> GetVersionAsync(string cliPath, Func<string, bool>? lineMatch, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(cliPath, new[] { "--version" }, stdin: null, TimeSpan.FromSeconds(8), ct)
            .ConfigureAwait(false);
        var lines = $"{stdout}\n{stderr}".Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var version = lineMatch is null
            ? lines.FirstOrDefault()
            : (lines.FirstOrDefault(lineMatch) ?? lines.FirstOrDefault());
        if (code == 0 && version is not null) return version;
        throw new InvalidOperationException(
            $"Version check failed with exit {code}: {(stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim())}");
    }

    private static ProcessStartInfo NewStartInfo(
        string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        if (env is not null)
            foreach (var (key, value) in env) psi.Environment[key] = value;
        return psi;
    }

    /// <summary>
    /// Spawn <paramref name="cliPath"/> with <paramref name="args"/>, optionally writing
    /// <paramref name="stdin"/>, and collect stdout/stderr until exit or <paramref name="timeout"/>.
    /// Returns the exit code (-1 if killed on timeout). <paramref name="env"/> entries are
    /// layered onto the inherited environment.
    /// </summary>
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string cliPath, IReadOnlyList<string> args, string? stdin, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = NewStartInfo(cliPath, args, env);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not spawn \"{cliPath}\": {ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (ct.IsCancellationRequested) throw;
            return (-1, stdout.ToString(), $"Timed out after {timeout.TotalSeconds:0}s.\n{stderr}");
        }

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static string ToWire(this CliSource source) => source switch
    {
        CliSource.Override => "override",
        CliSource.Env => "env",
        CliSource.Candidate => "candidate",
        CliSource.Where => "where",
        CliSource.Path => "path",
        _ => "fallback",
    };
}
