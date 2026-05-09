using System.Diagnostics;
using Tap.Core.Cloudflare;

namespace Tap.Cli.Commands;

/// <summary>
/// Tailscale Funnel runner for the <c>tap</c> CLI. Supports two daemon modes:
/// <list type="bullet">
///   <item><b>System</b>: shell out to the host's <c>tailscale</c> CLI against the system
///   <c>tailscaled</c>. Cheap; the node and ACL setup are pre-existing.</item>
///   <item><b>Ephemeral</b>: spawn a per-session userspace <c>tailscaled</c> with a temp
///   state dir and authenticate via auth key. Node disappears at shutdown.</item>
/// </list>
/// </summary>
internal static class TailscaleCliRunner
{
    public sealed record FunnelHandle(
        string MagicDnsName,
        int Port,
        string PublicUrl,
        string? SocketPath,
        Process? DaemonProcess,
        string? StateDir,
        string? DockerContainer,
        bool PublicExpose);

    public static async Task<FunnelHandle> StartAsync(
        TailscaleDaemonMode daemonMode,
        TailscaleHostMode hostMode,
        int port,
        string upstream,
        string? authKey,
        string? loginServer,
        bool publicExpose,
        CancellationToken ct)
    {
        EnsureCliAvailable();

        if (daemonMode == TailscaleDaemonMode.Ephemeral)
        {
            if (string.IsNullOrWhiteSpace(authKey))
            {
                throw new ArgumentException(
                    "Tailscale ephemeral mode requires an auth key. Pass --tailscale-authkey, set TAILSCALE_AUTHKEY, or store one in the profile.",
                    nameof(authKey));
            }

            if (hostMode == TailscaleHostMode.Docker)
            {
                return await StartEphemeralDockerAsync(port, upstream, authKey, loginServer, publicExpose, ct);
            }

            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Tailscale ephemeral mode (process) is not supported on Windows from the CLI yet. "
                    + "Add --docker (the container variant works on Windows), drop the auth key for system mode, or run from an Aspire AppHost.");
            }
            return await StartEphemeralAsync(port, upstream, authKey, loginServer, publicExpose, ct);
        }

        return await StartSystemAsync(port, upstream, publicExpose, ct);
    }

    public static async Task StopAsync(FunnelHandle handle, CancellationToken ct = default)
    {
        // 1. Remove our funnel rule from whichever socket we used. Path-specific so other
        //    rules on the same port survive (matters most for system mode). Skip for Docker
        //    since the container is about to be force-removed anyway.
        if (handle.DockerContainer is null)
        {
            var socketArgs = handle.SocketPath is null ? Array.Empty<string>() : new[] { $"--socket={handle.SocketPath}" };
            var pathOff = socketArgs.Concat(["serve", $"--https={handle.Port}", "--set-path=/", "off"]).ToArray();
            var portOff = socketArgs.Concat(["funnel", $"--https={handle.Port}", "off"]).ToArray();
            var (ok, _, _) = await RunAsync(pathOff, ct, treatNonZeroAsError: false);
            if (!ok) await RunAsync(portOff, ct, treatNonZeroAsError: false);
        }

        // 2a. Tear down the Docker container (with --rm it cleans itself up on stop).
        if (handle.DockerContainer is not null)
        {
            await RunDockerAsync(["rm", "-f", handle.DockerContainer], ct);
        }

        // 2b. Or kill the userspace tailscaled if we spawned one as a host process.
        if (handle.DaemonProcess is { HasExited: false } daemon)
        {
            try { daemon.Kill(entireProcessTree: true); } catch { }
            try { await daemon.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { }
        }

        // 3. Best-effort cleanup of the temp state dir. Don't throw on failure — the OS will GC.
        if (!string.IsNullOrEmpty(handle.StateDir) && Directory.Exists(handle.StateDir))
        {
            try { Directory.Delete(handle.StateDir, recursive: true); } catch { }
        }
    }

    private static async Task<FunnelHandle> StartSystemAsync(int port, string upstream, bool publicExpose, CancellationToken ct)
    {
        var dns = await ResolveSelfDnsNameAsync(socketPath: null, ct)
            ?? throw new InvalidOperationException(
                "Could not resolve this node's MagicDNS name. Is tailscaled running and the node logged in? Try `tailscale up`.");

        // Redirect cert output into temp so we don't leave files in the user's CWD.
        var (certPath, keyPath) = TempCertPaths(dns);
        _ = await RunAsync(["cert", "--cert-file", certPath, "--key-file", keyPath, dns], ct, treatNonZeroAsError: false);
        var verb = publicExpose ? "funnel" : "serve";
        var (ok, _, stderr) = await RunAsync(
            [verb, "--bg", $"--https={port}", "--set-path=/", upstream], ct);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"tailscale {verb} failed:\n{stderr.Trim()}\n\n"
                + (publicExpose
                    ? "Common causes: tailnet ACL doesn't grant `funnel` to this node's tags, HTTPS certificates aren't enabled in admin console, or another rule on the same path."
                    : "Common causes: HTTPS certificates aren't enabled in admin console, or another rule on the same path.")
                + $"\nTest manually: tailscale {verb} --bg --https={port} --set-path=/ {upstream}");
        }

        return new FunnelHandle(dns, port, BuildPublicUrl(dns, port),
            SocketPath: null, DaemonProcess: null, StateDir: null, DockerContainer: null, PublicExpose: publicExpose);
    }

    private static async Task<FunnelHandle> StartEphemeralAsync(
        int port, string upstream, string authKey, string? loginServer, bool publicExpose, CancellationToken ct)
    {
        // Per-session state dir under tmp; tailscaled owns its own socket inside it.
        var stateDir = Path.Combine(Path.GetTempPath(), $"tap-cli-tailscaled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        var socketPath = Path.Combine(stateDir, "tailscaled.sock");

        Process? daemon = null;
        try
        {
            // 1. Resolve tailscaled. The macOS App Store / GUI client puts `tailscale` on PATH
            //    but hides `tailscaled` inside the app bundle, so PATH-lookup alone fails for
            //    GUI-installed users. Brew's `tailscale` package installs both.
            var tailscaledPath = ResolveTailscaledPath();

            var psi = new ProcessStartInfo(tailscaledPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--tun=userspace-networking");
            psi.ArgumentList.Add($"--statedir={stateDir}");
            psi.ArgumentList.Add($"--socket={socketPath}");
            psi.ArgumentList.Add("--verbose=0");

            daemon = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to spawn tailscaled at {tailscaledPath}.");
            _ = DrainSilently(daemon.StandardOutput);
            _ = DrainSilently(daemon.StandardError);

            // 2. Wait up to 30s for the LocalAPI socket to appear.
            await WaitForSocketAsync(socketPath, daemon, TimeSpan.FromSeconds(30), ct);

            // 3. Authenticate via auth key. `--reset` so a stale state dir doesn't fight us.
            var nodeName = "tap-cli-" + Guid.NewGuid().ToString("N")[..8];
            var upArgs = new List<string>
            {
                $"--socket={socketPath}", "up",
                $"--authkey={authKey}",
                $"--hostname={nodeName}",
                "--reset",
            };
            if (!string.IsNullOrWhiteSpace(loginServer)) upArgs.Add($"--login-server={loginServer}");
            var (upOk, _, upErr) = await RunAsync(upArgs.ToArray(), ct);
            if (!upOk)
            {
                throw new InvalidOperationException(
                    $"`tailscale up` (ephemeral) failed:\n{upErr.Trim()}\n\n"
                    + "Check that the auth key is valid, reusable if you've used it already, and that its tags grant the funnel ACL capability.");
            }

            // 4. Wait until the node reports Online with a resolvable MagicDNS name.
            var dns = await WaitForOnlineAsync(socketPath, TimeSpan.FromSeconds(60), ct)
                ?? throw new InvalidOperationException(
                    "Ephemeral tailscale node never came online within 60s. Check the daemon logs and that the auth key is valid.");

            var (certPath, keyPath) = TempCertPaths(dns);
            _ = await RunAsync(
                [$"--socket={socketPath}", "cert", "--cert-file", certPath, "--key-file", keyPath, dns],
                ct, treatNonZeroAsError: false);

            // 5. Configure serve (tailnet-only) or funnel (public) against the per-session daemon.
            var verb = publicExpose ? "funnel" : "serve";
            var (ok, _, stderr) = await RunAsync(
                [$"--socket={socketPath}", verb, "--bg", $"--https={port}", "--set-path=/", upstream], ct);
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"tailscale {verb} failed (ephemeral):\n{stderr.Trim()}\n\n"
                    + (publicExpose
                        ? "Most likely the node's tags don't have the `funnel` ACL capability."
                        : "Check the auth key is valid and the tailnet has HTTPS certificates enabled."));
            }

            return new FunnelHandle(dns, port, BuildPublicUrl(dns, port),
                socketPath, daemon, stateDir, DockerContainer: null, PublicExpose: publicExpose);
        }
        catch
        {
            // Roll back partial setup so we don't leak a daemon + state dir on failure.
            if (daemon is { HasExited: false }) { try { daemon.Kill(entireProcessTree: true); } catch { } }
            try { Directory.Delete(stateDir, recursive: true); } catch { }
            throw;
        }
    }

    private static async Task<FunnelHandle> StartEphemeralDockerAsync(
        int port, string upstream, string authKey, string? loginServer, bool publicExpose, CancellationToken ct)
    {
        EnsureDockerAvailable();

        // We drive `tailscale` inside the container via `docker exec` rather than bind-mounting
        // the LocalAPI socket. macOS Docker Desktop can't carry unix-socket endpoints across the
        // VM boundary — the host sees the file but can't actually dial it. `docker exec` works
        // on every platform.
        var containerUpstream = TailscaleHelpers.RewriteUpstreamForDocker(upstream);
        var containerName = $"tap-cli-ts-{Guid.NewGuid():N}"[..28];
        var nodeName = "tap-cli-" + Guid.NewGuid().ToString("N")[..8];

        var dockerArgs = new List<string>
        {
            "run", "-d", "--rm",
            "--name", containerName,
            "-e", $"TS_AUTHKEY={authKey}",
            "-e", "TS_USERSPACE=true",
            "-e", $"TS_HOSTNAME={nodeName}",
            "-e", "TS_EXTRA_ARGS=--reset",
        };
        if (!string.IsNullOrWhiteSpace(loginServer))
        {
            dockerArgs.Add("-e"); dockerArgs.Add($"TS_LOGIN_SERVER={loginServer}");
        }
        if (OperatingSystem.IsLinux())
        {
            dockerArgs.Add("--add-host"); dockerArgs.Add("host.docker.internal:host-gateway");
        }
        dockerArgs.Add("tailscale/tailscale:latest");

        var (started, _, stderr) = await RunDockerAsync(dockerArgs, ct);
        if (!started)
        {
            throw new InvalidOperationException(
                $"Failed to start tailscale/tailscale container:\n{stderr.Trim()}\n\n"
                + "Is Docker running? `docker info` should succeed.");
        }

        try
        {
            // Container's entrypoint runs `tailscale up --authkey=...` automatically.
            // Wait for it to land in Running state with a resolvable MagicDNS name.
            var dns = await WaitForOnlineDockerAsync(containerName, TimeSpan.FromSeconds(60), ct)
                ?? throw new InvalidOperationException(
                    "Ephemeral tailscale (Docker) node never came online within 60s. "
                    + $"Inspect the container: docker logs {containerName}");

            // Cert files land inside the container's /tmp; nothing to redirect on the host.
            _ = await RunDockerAsync(
                ["exec", containerName, "tailscale", "cert",
                 "--cert-file", $"/tmp/{dns}.crt", "--key-file", $"/tmp/{dns}.key", dns], ct);

            var verb = publicExpose ? "funnel" : "serve";
            var (ok, _, ferr) = await RunDockerAsync(
                ["exec", containerName, "tailscale", verb, "--bg",
                 $"--https={port}", "--set-path=/", containerUpstream], ct);
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"tailscale {verb} failed (Docker):\n{ferr.Trim()}\n\n"
                    + (publicExpose
                        ? "Most likely the auth key's tags don't have the `funnel` ACL capability."
                        : "Check the auth key + tailnet HTTPS settings."));
            }

            return new FunnelHandle(dns, port, BuildPublicUrl(dns, port),
                SocketPath: null, DaemonProcess: null, StateDir: null, DockerContainer: containerName,
                PublicExpose: publicExpose);
        }
        catch
        {
            await RunDockerAsync(["rm", "-f", containerName], CancellationToken.None);
            throw;
        }
    }

    private static async Task<string?> WaitForOnlineDockerAsync(string containerName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // Liveness check: container has to still be Running.
            var (_, runningOut, _) = await RunDockerAsync(
                ["inspect", "-f", "{{.State.Running}}", containerName], ct);
            if (!runningOut.Contains("true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"tailscale container {containerName} exited before reporting Online. "
                    + $"Inspect with: docker logs {containerName}");
            }

            var (ok, stdout, _) = await RunDockerAsync(
                ["exec", containerName, "tailscale", "status", "--json", "--peers=false"], ct);
            // tailscale status --json --peers=false is pretty-printed (e.g. `"Online": true`),
            // so we can't rely on a Contains match without whitespace.
            if (ok && System.Text.RegularExpressions.Regex.IsMatch(stdout, "\"Online\"\\s*:\\s*true"))
            {
                var dns = ExtractDnsName(stdout);
                if (!string.IsNullOrEmpty(dns)) return dns;
            }
            await Task.Delay(750, ct);
        }
        return null;
    }

    private static void EnsureDockerAvailable()
    {
        if (!TryProbe("docker"))
        {
            throw new InvalidOperationException(
                "`docker` CLI not found on PATH or Docker daemon not running. "
                + "Start Docker Desktop (macOS/Windows) or `dockerd` (Linux) and retry. "
                + "Alternative: install brew tailscale and drop --docker.");
        }
    }

    private static async Task<(bool Ok, string Stdout, string Stderr)> RunDockerAsync(
        IEnumerable<string> args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode == 0, await stdoutTask, await stderrTask);
        }
        catch (Exception ex) { return (false, string.Empty, ex.Message); }
    }

    private static async Task WaitForSocketAsync(string socketPath, Process daemon, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (daemon.HasExited)
            {
                throw new InvalidOperationException(
                    $"tailscaled exited (code {daemon.ExitCode}) before its LocalAPI socket appeared. "
                    + "Run `tailscaled --tun=userspace-networking --statedir=<dir> --socket=<dir>/sock` manually to see the error.");
            }
            if (File.Exists(socketPath)) return;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"tailscaled didn't create its socket at {socketPath} within {timeout.TotalSeconds:0}s.");
    }

    private static async Task<string?> WaitForOnlineAsync(string socketPath, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, stdout, _) = await RunAsync(
                [$"--socket={socketPath}", "status", "--json", "--peers=false"], ct, treatNonZeroAsError: false);
            // tailscale status --json --peers=false is pretty-printed (e.g. `"Online": true`),
            // so we can't rely on a Contains match without whitespace.
            if (ok && System.Text.RegularExpressions.Regex.IsMatch(stdout, "\"Online\"\\s*:\\s*true"))
            {
                var dns = ExtractDnsName(stdout);
                if (!string.IsNullOrEmpty(dns)) return dns;
            }
            await Task.Delay(750, ct);
        }
        return null;
    }

    private static (string CertPath, string KeyPath) TempCertPaths(string dns)
    {
        var tmp = Path.GetTempPath();
        return (Path.Combine(tmp, $"{dns}.crt"), Path.Combine(tmp, $"{dns}.key"));
    }

    private static string BuildPublicUrl(string dns, int port) =>
        port == 443 ? $"https://{dns}" : $"https://{dns}:{port}";

    private static string ResolveTailscaledPath()
    {
        // 1. Honor explicit override for non-standard installs.
        var fromEnv = Environment.GetEnvironmentVariable("TAILSCALED_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (!File.Exists(fromEnv))
            {
                throw new InvalidOperationException(
                    $"TAILSCALED_PATH points at '{fromEnv}' but no file exists there.");
            }
            return fromEnv;
        }

        // 2. Try PATH (works for brew, apt, dnf installs).
        if (TryProbe("tailscaled")) return "tailscaled";

        // 3. Common locations the OS package managers + GUI installers use.
        IEnumerable<string> candidates = OperatingSystem.IsMacOS()
            ? new[]
            {
                "/opt/homebrew/bin/tailscaled",            // Apple Silicon brew
                "/usr/local/bin/tailscaled",               // Intel brew
                "/usr/local/sbin/tailscaled",
                "/Applications/Tailscale.app/Contents/MacOS/Tailscaled",  // GUI bundle (not normally invokable but worth trying)
            }
            : OperatingSystem.IsLinux()
                ? new[]
                {
                    "/usr/sbin/tailscaled",
                    "/usr/local/sbin/tailscaled",
                    "/usr/bin/tailscaled",
                    "/usr/local/bin/tailscaled",
                }
                : Array.Empty<string>();

        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }

        var hint = OperatingSystem.IsMacOS()
            ? "Install via `brew install tailscale` (the App Store / GUI client doesn't expose tailscaled on PATH)."
            : "Install Tailscale from https://tailscale.com/download/linux or your distribution's package manager.";
        throw new InvalidOperationException(
            "Couldn't find a `tailscaled` binary for ephemeral mode. " + hint
            + " You can also point at a custom location with `TAILSCALED_PATH=/path/to/tailscaled`.");
    }

    private static bool TryProbe(string fileName)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void EnsureCliAvailable()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("tailscale", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (probe is null) throw new InvalidOperationException("tailscale CLI not found on PATH.");
            probe.WaitForExit(5000);
            if (!probe.HasExited || probe.ExitCode != 0)
            {
                throw new InvalidOperationException("tailscale CLI didn't respond to `tailscale version`.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "tailscale CLI not found on PATH. Install Tailscale (https://tailscale.com/download) and ensure `tailscale` is on PATH.", ex);
        }
    }

    private static async Task<string?> ResolveSelfDnsNameAsync(string? socketPath, CancellationToken ct)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(socketPath)) args.Add($"--socket={socketPath}");
        args.AddRange(["status", "--json", "--peers=false"]);
        var (ok, stdout, _) = await RunAsync(args.ToArray(), ct, treatNonZeroAsError: false);
        if (!ok || string.IsNullOrWhiteSpace(stdout)) return null;
        return ExtractDnsName(stdout);
    }

    private static string? ExtractDnsName(string statusJson)
    {
        var marker = "\"DNSName\"";
        var idx = statusJson.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var colon = statusJson.IndexOf(':', idx + marker.Length);
        if (colon < 0) return null;
        var openQuote = statusJson.IndexOf('"', colon + 1);
        if (openQuote < 0) return null;
        var closeQuote = statusJson.IndexOf('"', openQuote + 1);
        if (closeQuote < 0) return null;
        return statusJson[(openQuote + 1)..closeQuote].TrimEnd('.');
    }

    private static async Task<(bool Ok, string Stdout, string Stderr)> RunAsync(
        string[] args, CancellationToken ct, bool treatNonZeroAsError = true)
    {
        try
        {
            var psi = new ProcessStartInfo("tailscale")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var ok = proc.ExitCode == 0;
            if (!ok && treatNonZeroAsError) return (false, stdout, stderr);
            return (ok, stdout, stderr);
        }
        catch (Exception ex) { return (false, string.Empty, ex.Message); }
    }

    private static Task DrainSilently(StreamReader r) => Task.Run(async () =>
    {
        try { while (await r.ReadLineAsync() is not null) { /* discard */ } }
        catch { }
    });
}
