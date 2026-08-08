using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Tunnels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tap.Core.Cloudflare;

namespace Aspire.Hosting.Tailscale;

#pragma warning disable CS0618 // Eventing-based replacement is not yet wired; lifecycle hook works fine today.
internal sealed class TailscaleLifecycleHook(
    ILogger<TailscaleLifecycleHook> logger,
    ResourceLoggerService resourceLoggers,
    ResourceNotificationService resourceNotifications,
    IHostApplicationLifetime applicationLifetime)
    : IDistributedApplicationLifecycleHook
{
    private static readonly Regex HostnamePattern =
        new(@"TAP_TAILSCALE_HOSTNAME=([a-z0-9.\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Auth-key env files written for Docker mode; removed on shutdown.</summary>
    private readonly List<string> _secretFiles = [];

    private int _cleanedUp;

    public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        var tunnels = appModel.Resources.OfType<TailscaleFunnelResource>().ToList();
        if (tunnels.Count == 0)
        {
            return;
        }

        // Docker-mode tunnels run their own `tailscale` inside the container; the host CLI
        // is only required when at least one tunnel uses host-process mode (system or
        // ephemeral process). Skip the check entirely when everything is in containers —
        // that's exactly the use case Docker mode was added for.
        if (tunnels.Any(t => t.HostMode != TailscaleHostMode.Docker))
        {
            EnsureTailscaleAvailable();
        }

        foreach (var tunnel in tunnels)
        {
            ConfigureTunnel(tunnel);
            ValidateConfigured(tunnel);
            LogSummary(tunnel);

            // Watcher runs for the lifetime of the AppHost; we don't await it.
            _ = WatchHostnameAsync(tunnel, cancellationToken);
        }

        RegisterShutdownCleanup(tunnels);
    }
#pragma warning restore CS0618

    /// <summary>
    /// Belt-and-braces teardown for public funnels on the system daemon. The bootstrapper's
    /// EXIT trap covers a graceful stop, but a crashed or force-quit AppHost never runs it and
    /// the system <c>tailscaled</c> outlives the process — leaving the funnel serving the
    /// internet until somebody notices. Ephemeral and Docker nodes leave the tailnet with their
    /// own daemon, so they need nothing here. A SIGKILL still bypasses this; the system
    /// bootstrapper clears a stale rule on the next start to cover that case.
    /// </summary>
    private void RegisterShutdownCleanup(List<TailscaleFunnelResource> tunnels)
    {
        var funnels = tunnels
            .Where(t => t.PublicExpose && !t.UseEphemeralDaemon && t.HostMode != TailscaleHostMode.Docker)
            .ToList();

        applicationLifetime.ApplicationStopping.Register(Cleanup);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        void Cleanup()
        {
            if (Interlocked.Exchange(ref _cleanedUp, 1) != 0) return;

            foreach (var tunnel in funnels)
            {
                TurnFunnelOff(tunnel);
            }

            foreach (var path in _secretFiles)
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    private void TurnFunnelOff(TailscaleFunnelResource tunnel)
    {
        try
        {
            var psi = new ProcessStartInfo("tailscale")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("funnel");
            psi.ArgumentList.Add($"--https={tunnel.FunnelPort.ToString(CultureInfo.InvariantCulture)}");
            psi.ArgumentList.Add("off");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            logger.LogInformation(
                "Tailscale Funnel '{Name}': public exposure on port {Port} turned off.",
                tunnel.Name, tunnel.FunnelPort);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not turn off Tailscale Funnel '{Name}'. Run 'tailscale funnel --https={Port} off' by hand — the URL is public until you do.",
                tunnel.Name, tunnel.FunnelPort);
        }
    }

    private void ConfigureTunnel(TailscaleFunnelResource tunnel)
    {
        if (tunnel.IngressEntries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Tailscale Funnel '{tunnel.Name}' has no upstream attached. "
                + "Call .WithTap(tap) on the upstream resource where tap is bound to this funnel.");
        }

        if (tunnel.UseEphemeralDaemon)
        {
            if (tunnel.HostMode == TailscaleHostMode.Docker)
            {
                // Docker mode: drive `tailscale` via `docker exec` (bind-mounted unix sockets
                // don't survive macOS Docker Desktop's VM boundary). No state dir needed.
                tunnel.DockerContainerName = $"tap-ts-{Guid.NewGuid():N}"[..28];
            }
            else
            {
                var stateDir = Path.Combine(Path.GetTempPath(), $"tap-ts-{tunnel.Name}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stateDir);
                tunnel.StateDir = stateDir;
                tunnel.SocketPath = Path.Combine(stateDir, "tailscaled.sock");
            }
        }

        ConfigureDaemonResource(tunnel);

        var scriptPath = WriteBootstrapperScript(tunnel);
        ConfigureFunnelArgs(tunnel, scriptPath);
        ConfigureAuthKeyEnvironment(tunnel);
    }

    /// <summary>
    /// Hands the ephemeral auth key to the bootstrapper through its environment. As a script
    /// argument it sat in the host process list for the whole session, readable by every local
    /// user; the key is long-lived enough (often reusable) that this is worth avoiding.
    /// </summary>
    private static void ConfigureAuthKeyEnvironment(TailscaleFunnelResource tunnel)
    {
        if (!tunnel.UseEphemeralDaemon || tunnel.HostMode == TailscaleHostMode.Docker) return;

        tunnel.Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
        {
            ctx.EnvironmentVariables["TS_AUTHKEY"] = tunnel.AuthKey!;
        }));
    }

    private void ConfigureDaemonResource(TailscaleFunnelResource tunnel)
    {
        if (!tunnel.UseEphemeralDaemon || tunnel.Daemon is null) return;

        var daemon = tunnel.Daemon;
        daemon.StateDir = tunnel.StateDir;
        daemon.SocketPath = tunnel.SocketPath;
        daemon.DockerContainerName = tunnel.DockerContainerName;

        var args = daemon.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToList();
        foreach (var existing in args) daemon.Annotations.Remove(existing);

        daemon.Annotations.Add(new CommandLineArgsCallbackAnnotation(ctx =>
        {
            if (tunnel.HostMode == TailscaleHostMode.Docker)
            {
                AppendDockerRunArgs(ctx.Args, tunnel);
            }
            else
            {
                // Userspace networking removes the need for root/CAP_NET_ADMIN; the cost is a
                // slower TCP path on first connection. Acceptable for development.
                ctx.Args.Add("--tun=userspace-networking");
                ctx.Args.Add($"--statedir={tunnel.StateDir}");
                ctx.Args.Add($"--socket={tunnel.SocketPath}");
                ctx.Args.Add("--verbose=0");
            }
            return Task.CompletedTask;
        }));
    }

    private void AppendDockerRunArgs(IList<object> args, TailscaleFunnelResource tunnel)
    {
        // `tailscale/tailscale` runs `tailscaled` and then `tailscale up --authkey=$TS_AUTHKEY`
        // automatically. We drive funnel config via `docker exec` rather than bind-mounting
        // the LocalAPI socket (the latter doesn't work on macOS Docker Desktop).
        args.Add("run");
        args.Add("--rm");
        args.Add("--name"); args.Add(tunnel.DockerContainerName!);
        // --env-file, not `-e TS_AUTHKEY=...`: the latter puts the auth key in the host process
        // list for as long as `docker run` lives, readable by every local user.
        args.Add("--env-file"); args.Add(WriteDockerEnvFile(tunnel));
        if (OperatingSystem.IsLinux())
        {
            args.Add("--add-host"); args.Add("host.docker.internal:host-gateway");
        }
        args.Add(tunnel.DockerImage);
    }

    /// <summary>
    /// Writes the container's environment (including the auth key) to a 0600 temp file. The
    /// path is recorded so shutdown can delete it.
    /// </summary>
    private string WriteDockerEnvFile(TailscaleFunnelResource tunnel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tap-ts-{tunnel.Name}-{Guid.NewGuid():N}.env");

        // Create empty and tighten the mode before the secret goes in, so it is never briefly
        // world-readable.
        using (File.Create(path)) { }
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort — a filesystem that can't express the mode is not fatal */ }
        }

        var lines = new List<string>
        {
            $"TS_AUTHKEY={tunnel.AuthKey}",
            "TS_USERSPACE=true",
            $"TS_HOSTNAME={SafeNodeName(tunnel.Name)}",
            "TS_EXTRA_ARGS=--reset",
        };
        if (!string.IsNullOrWhiteSpace(tunnel.LoginServer))
        {
            lines.Add($"TS_LOGIN_SERVER={tunnel.LoginServer}");
        }
        File.WriteAllLines(path, lines);

        _secretFiles.Add(path);
        return path;
    }

    private static void ConfigureFunnelArgs(TailscaleFunnelResource tunnel, string scriptPath)
    {
        var args = tunnel.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToList();
        foreach (var existing in args) tunnel.Annotations.Remove(existing);

        tunnel.Annotations.Add(new CommandLineArgsCallbackAnnotation(ctx =>
        {
            var ingress = tunnel.IngressEntries[0];
            // If a tap is attached, route Funnel → tap proxy → upstream so the inspector
            // captures every request. Without a tap, fall back to the upstream directly.
            var upstream = tunnel.InspectorProxyPort.HasValue
                ? $"http://localhost:{tunnel.InspectorProxyPort.Value}"
                : TapExtensions.ResolveLocalUrl(
                    new TapIngressEntry(ingress.Hostname, ingress.Target, ingress.EndpointName));

            if (tunnel.HostMode == TailscaleHostMode.Docker)
            {
                upstream = TailscaleHelpers.RewriteUpstreamForDocker(upstream);
            }

            ctx.Args.Add(scriptPath);
            var expose = tunnel.PublicExpose ? "funnel" : "serve";

            if (tunnel.HostMode == TailscaleHostMode.Docker)
            {
                // Docker bootstrapper: container name + port + upstream + expose mode.
                ctx.Args.Add(tunnel.DockerContainerName!);
                ctx.Args.Add(tunnel.FunnelPort.ToString(CultureInfo.InvariantCulture));
                ctx.Args.Add(upstream);
                ctx.Args.Add(expose);
            }
            else if (tunnel.UseEphemeralDaemon)
            {
                // The auth key is deliberately absent here — it arrives via TS_AUTHKEY so it
                // stays out of the host process list. See ConfigureAuthKeyEnvironment.
                ctx.Args.Add(tunnel.SocketPath!);
                ctx.Args.Add(tunnel.FunnelPort.ToString(CultureInfo.InvariantCulture));
                ctx.Args.Add(upstream);
                ctx.Args.Add(SafeNodeName(tunnel.Name));
                ctx.Args.Add(tunnel.LoginServer ?? string.Empty);
                ctx.Args.Add(expose);
            }
            else
            {
                ctx.Args.Add(tunnel.FunnelPort.ToString(CultureInfo.InvariantCulture));
                ctx.Args.Add(upstream);
                ctx.Args.Add(expose);
            }
            return Task.CompletedTask;
        }));
    }

    private static string SafeNodeName(string raw)
    {
        // tailscale `--hostname` is restrictive: lowercase, digits, hyphens.
        var span = raw.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var name = new string(span).Trim('-');
        return string.IsNullOrEmpty(name) ? "tap" : name;
    }

    private async Task WatchHostnameAsync(TailscaleFunnelResource tunnel, CancellationToken ct)
    {
        try
        {
            await foreach (var batch in resourceLoggers.WatchAsync(tunnel).WithCancellation(ct))
            {
                foreach (var line in batch)
                {
                    var match = HostnamePattern.Match(line.Content);
                    if (!match.Success) continue;

                    var dns = match.Groups[1].Value.TrimEnd('.');
                    if (string.Equals(tunnel.MagicDnsName, dns, StringComparison.Ordinal))
                    {
                        return;
                    }

                    tunnel.MagicDnsName = dns;
                    var publicUrl = TailscaleExtensions.BuildPublicUrl(tunnel);
                    logger.LogInformation("Tailscale Funnel '{Name}' is live: {Url}", tunnel.Name, publicUrl);

                    if (tunnel.IngressEntries.Count > 0)
                    {
                        tunnel.IngressEntries[0].Hostname = dns;
                        tunnel.IngressEntries[0].PublicUrl = publicUrl;
                    }

                    foreach (var entry in tunnel.IngressEntries)
                    {
                        foreach (var ann in entry.Target.Annotations.OfType<TapTunnelAnnotation>())
                        {
                            if (ReferenceEquals(ann.Tunnel, tunnel))
                            {
                                ann.Hostname = dns;
                            }
                        }
                    }

                    await resourceNotifications.PublishUpdateAsync(tunnel, snapshot =>
                    {
                        var existing = snapshot.Urls.FirstOrDefault(u => u.Name == "funnel");
                        var newUrl = new UrlSnapshot(Name: "funnel", Url: publicUrl, IsInternal: false);
                        var urls = existing is not null
                            ? snapshot.Urls.Replace(existing, newUrl)
                            : snapshot.Urls.Add(newUrl);
                        return snapshot with { Urls = urls };
                    });

                    if (tunnel.AttachedTap is { } tap)
                    {
                        await resourceNotifications.PublishUpdateAsync(tap.Resource, snapshot =>
                        {
                            var proxy = snapshot.Urls.FirstOrDefault(u => u.Name == "proxy");
                            if (proxy is null) return snapshot;
                            var patched = proxy with
                            {
                                Url = publicUrl,
                                DisplayProperties = new UrlDisplayPropertiesSnapshot("Public URL", 0),
                            };
                            return snapshot with { Urls = snapshot.Urls.Replace(proxy, patched) };
                        });
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to watch Tailscale tunnel '{Name}' logs.", tunnel.Name);
        }
    }

    private static void ValidateConfigured(TailscaleFunnelResource tunnel)
    {
        if (tunnel.UseEphemeralDaemon && string.IsNullOrWhiteSpace(tunnel.AuthKey))
        {
            throw new InvalidOperationException(
                $"Tailscale tunnel '{tunnel.Name}' is in ephemeral mode but no auth key was supplied. Call .WithEphemeralDaemon(authKey).");
        }
    }

    private void LogSummary(TailscaleFunnelResource tunnel)
    {
        var mode = tunnel.UseEphemeralDaemon ? "ephemeral userspace daemon" : "system daemon";
        var ingress = tunnel.IngressEntries.FirstOrDefault();
        logger.LogInformation(
            "Tailscale Funnel '{Name}' configured ({Mode}, port {Port}). Upstream: {Target}. Public URL will be assigned on startup.",
            tunnel.Name, mode, tunnel.FunnelPort, ingress?.Target.Name ?? "(none)");

        if (tunnel.InspectorProxyPort is { } inspectorPort)
        {
            logger.LogInformation(
                "HTTP Inspector enabled. UI: http://localhost:{Ui}  |  Proxy port: {Proxy}",
                tunnel.InspectorUiPort,
                inspectorPort);
        }
    }

    private void EnsureTailscaleAvailable()
    {
        // We just check `tailscale` (CLI) is on PATH. tailscaled for ephemeral mode is
        // checked when it starts as its own resource — Aspire surfaces that error.
        var found = ProcessExists("tailscale", "--version");
        if (!found)
        {
            throw new InvalidOperationException(
                "tailscale CLI not found on PATH. Install Tailscale (https://tailscale.com/download) "
                + "and ensure 'tailscale' is on PATH before launching the AppHost.");
        }
    }

    private static bool ProcessExists(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string WriteBootstrapperScript(TailscaleFunnelResource tunnel)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: only system + Docker variants are supported. Ephemeral process mode
            // would require navigating the Tailscale service model and is left as future work.
            if (tunnel.UseEphemeralDaemon && tunnel.HostMode != TailscaleHostMode.Docker)
            {
                throw new PlatformNotSupportedException(
                    "Tailscale ephemeral process mode is not yet supported on Windows. "
                    + "Use Docker mode (AddTailscaleFunnel(hostMode: TailscaleHostMode.Docker)) "
                    + "or .WithSystemDaemon().");
            }
            return WriteWindowsSystemScript(tunnel);
        }

        var path = Path.Combine(Path.GetTempPath(), $"tap-ts-{tunnel.Name}-{Guid.NewGuid():N}.sh");
        var contents = (tunnel.UseEphemeralDaemon, tunnel.HostMode) switch
        {
            (true, TailscaleHostMode.Docker) => UnixDockerScript(),
            (true, _) => UnixEphemeralScript(),
            _ => UnixSystemScript(),
        };
        File.WriteAllText(path, contents);
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch { }
        return path;
    }

    private static string WriteWindowsSystemScript(TailscaleFunnelResource tunnel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tap-ts-{tunnel.Name}-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, WindowsSystemScript());
        return path;
    }

    private const string UnixSystemPreamble = """
#!/usr/bin/env bash
set -e
PORT="$1"
UPSTREAM="$2"
EXPOSE="$3"   # "funnel" (public) or "serve" (tailnet-only)

if ! command -v tailscale >/dev/null 2>&1; then
  echo "ERROR: tailscale CLI not found on PATH (PATH=$PATH)." >&2
  exit 127
fi

# `--peers=false` keeps Self in the JSON but drops the (possibly dozens of) peer
# entries — funnel-ingress-node peers each have a DNSName which would otherwise
# get picked up by the grep below.
STATUS_FILE=$(mktemp)
ERR_FILE=$(mktemp)
if ! tailscale status --json --peers=false >"$STATUS_FILE" 2>"$ERR_FILE"; then
  echo "ERROR: 'tailscale status --json --peers=false' failed:" >&2
  cat "$ERR_FILE" >&2
  echo "(tailscale binary: $(command -v tailscale))" >&2
  exit 5
fi
STATUS=$(cat "$STATUS_FILE")
rm -f "$STATUS_FILE" "$ERR_FILE"

DNS=$(printf '%s' "$STATUS" | grep -oE '"DNSName":[[:space:]]*"[^"]*"' | head -1 | sed -E 's/"DNSName":[[:space:]]*"//;s/"$//;s/\.$//')
if [ -z "$DNS" ]; then
  echo "ERROR: failed to resolve this node's MagicDNS name from tailscale status." >&2
  echo "First 400 bytes of status:" >&2
  printf '%s' "$STATUS" | head -c 400 >&2
  echo >&2
  exit 3
fi

# Pre-warm cert into the temp dir so cert files don't pollute the AppHost's CWD.
TS_CERT_DIR="${TMPDIR:-/tmp}"
tailscale cert --cert-file="$TS_CERT_DIR/$DNS.crt" --key-file="$TS_CERT_DIR/$DNS.key" "$DNS" >/dev/null 2>&1 || true

# A previous run that was hard-killed never ran its EXIT trap, and the system daemon keeps
# the rule. Funnel is a per-port flag, so a tailnet-only run would otherwise inherit that
# port's public exposure and quietly serve the internet.
if [ "$EXPOSE" != "funnel" ]; then
  tailscale funnel --https="$PORT" off >/dev/null 2>&1 || true
fi

if [ "$EXPOSE" = "funnel" ]; then
  echo "WARNING: this URL is publicly reachable on the internet. Scanners typically find" >&2
  echo "         new funnel hostnames within minutes. Pair with auth (header/CIDR/OIDC)." >&2
  tailscale funnel --bg --https="$PORT" --set-path=/ "$UPSTREAM"
else
  tailscale serve --bg --https="$PORT" --set-path=/ "$UPSTREAM"
fi

echo "TAP_TAILSCALE_HOSTNAME=$DNS"
echo "TAP_TAILSCALE_READY $DNS ($EXPOSE)"

# Path-specific cleanup so we don't blow away other rules the user has on this port.
cleanup() {
  tailscale serve --https="$PORT" --set-path=/ off >/dev/null 2>&1 \
    || tailscale funnel --https="$PORT" off >/dev/null 2>&1 \
    || true
}
trap cleanup EXIT TERM INT
# Block forever. `sleep infinity` is GNU-only (macOS BSD sleep rejects it),
# so use `tail -f /dev/null` which blocks indefinitely on every Unix.
tail -f /dev/null
""";

    private static string UnixSystemScript() => UnixSystemPreamble;

    private const string UnixEphemeralPreamble = """
#!/usr/bin/env bash
set -e
SOCKET="$1"
PORT="$2"
UPSTREAM="$3"
NODE_NAME="$4"
LOGIN_SERVER="${5:-}"
EXPOSE="${6:-serve}"
# Comes in through the environment, not argv, so it never shows up in `ps`.
AUTHKEY="${TS_AUTHKEY:-}"

if ! command -v tailscale >/dev/null 2>&1; then
  echo "ERROR: tailscale CLI not found on PATH." >&2
  exit 127
fi

if [ -z "$AUTHKEY" ]; then
  echo "ERROR: TS_AUTHKEY was not passed to the bootstrapper." >&2
  exit 6
fi

# Wait for the daemon's LocalAPI socket to appear (up to 30s).
for i in $(seq 1 60); do
  if [ -S "$SOCKET" ]; then break; fi
  sleep 0.5
done
if [ ! -S "$SOCKET" ]; then
  echo "ERROR: tailscaled socket never appeared at $SOCKET." >&2
  exit 4
fi

LS_FLAG=""
if [ -n "$LOGIN_SERVER" ]; then LS_FLAG="--login-server=$LOGIN_SERVER"; fi
tailscale --socket="$SOCKET" up --authkey="$AUTHKEY" --hostname="$NODE_NAME" --reset $LS_FLAG

# Wait until the node reports Online with a resolvable MagicDNS name.
# `--peers=false` so we don't accidentally grep a peer's DNSName.
DNS=""
for i in $(seq 1 60); do
  STATUS=$(tailscale --socket="$SOCKET" status --json --peers=false 2>/dev/null || echo '{}')
  # Pretty-printed JSON (`"Online": true`) AND compact form (`"Online":true`) — match both.
  case "$STATUS" in
    *'"Online":true'*|*'"Online": true'*)
      DNS=$(printf '%s' "$STATUS" | grep -oE '"DNSName":[[:space:]]*"[^"]*"' | head -1 | sed -E 's/"DNSName":[[:space:]]*"//;s/"$//;s/\.$//')
      if [ -n "$DNS" ]; then break; fi
      ;;
  esac
  sleep 1
done

if [ -z "$DNS" ]; then
  echo "ERROR: ephemeral tailscale node never came online." >&2
  echo "Check that the auth key is valid and the daemon could reach controlplane.tailscale.com." >&2
  exit 2
fi

TS_CERT_DIR="${TMPDIR:-/tmp}"
tailscale --socket="$SOCKET" cert --cert-file="$TS_CERT_DIR/$DNS.crt" --key-file="$TS_CERT_DIR/$DNS.key" "$DNS" >/dev/null 2>&1 || true

if [ "$EXPOSE" = "funnel" ]; then
  echo "WARNING: this URL is publicly reachable on the internet — pair with auth." >&2
  tailscale --socket="$SOCKET" funnel --bg --https="$PORT" --set-path=/ "$UPSTREAM"
else
  tailscale --socket="$SOCKET" serve --bg --https="$PORT" --set-path=/ "$UPSTREAM"
fi

echo "TAP_TAILSCALE_HOSTNAME=$DNS"
echo "TAP_TAILSCALE_READY $DNS ($EXPOSE)"

# Path-specific cleanup so other rules on the port survive even when the daemon outlives this script.
cleanup() {
  tailscale --socket="$SOCKET" serve --https="$PORT" --set-path=/ off >/dev/null 2>&1 \
    || tailscale --socket="$SOCKET" funnel --https="$PORT" off >/dev/null 2>&1 \
    || true
}
trap cleanup EXIT TERM INT
# Block forever (see system-mode preamble for why we don't use `sleep infinity`).
tail -f /dev/null
""";

    private static string UnixEphemeralScript() => UnixEphemeralPreamble;

    private const string UnixDockerPreamble = """
#!/usr/bin/env bash
set -e
CONTAINER="$1"
PORT="$2"
UPSTREAM="$3"
EXPOSE="${4:-serve}"

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: docker CLI not found on PATH." >&2
  exit 127
fi

# Wait for the node to come Online. The container's entrypoint runs `tailscale up`
# automatically from TS_AUTHKEY. We drive everything via `docker exec` because
# bind-mounted unix sockets don't work across macOS Docker Desktop's VM boundary.
DNS=""
for i in $(seq 1 120); do
  if ! docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null | grep -q true; then
    echo "ERROR: tailscale container '$CONTAINER' exited before reporting Online." >&2
    echo "Inspect: docker logs $CONTAINER" >&2
    exit 5
  fi
  STATUS=$(docker exec "$CONTAINER" tailscale status --json --peers=false 2>/dev/null || echo '{}')
  # Pretty-printed JSON (`"Online": true`) AND compact form (`"Online":true`) — match both.
  case "$STATUS" in
    *'"Online":true'*|*'"Online": true'*)
      DNS=$(printf '%s' "$STATUS" | grep -oE '"DNSName":[[:space:]]*"[^"]*"' | head -1 | sed -E 's/"DNSName":[[:space:]]*"//;s/"$//;s/\.$//')
      if [ -n "$DNS" ]; then break; fi
      ;;
  esac
  sleep 1
done

if [ -z "$DNS" ]; then
  echo "ERROR: ephemeral tailscale (Docker) node never came online within 120s." >&2
  echo "Inspect: docker logs $CONTAINER" >&2
  exit 2
fi

# Pre-warm cert inside the container — its files don't leak to the host.
docker exec "$CONTAINER" tailscale cert --cert-file="/tmp/$DNS.crt" --key-file="/tmp/$DNS.key" "$DNS" >/dev/null 2>&1 || true

if [ "$EXPOSE" = "funnel" ]; then
  echo "WARNING: this URL is publicly reachable on the internet — pair with auth." >&2
  docker exec "$CONTAINER" tailscale funnel --bg --https="$PORT" --set-path=/ "$UPSTREAM"
else
  docker exec "$CONTAINER" tailscale serve --bg --https="$PORT" --set-path=/ "$UPSTREAM"
fi

echo "TAP_TAILSCALE_HOSTNAME=$DNS"
echo "TAP_TAILSCALE_READY $DNS ($EXPOSE)"

# Container is the daemon; --rm tears it down when Aspire kills the docker run, but if the
# image somehow becomes shared we still want the path-specific rule cleaned up.
cleanup() {
  docker exec "$CONTAINER" tailscale serve --https="$PORT" --set-path=/ off >/dev/null 2>&1 \
    || docker exec "$CONTAINER" tailscale funnel --https="$PORT" off >/dev/null 2>&1 \
    || true
}
trap cleanup EXIT TERM INT
tail -f /dev/null
""";

    private static string UnixDockerScript() => UnixDockerPreamble;

    private const string WindowsSystemBody = """
$ErrorActionPreference = 'Stop'
$Port = $args[0]
$Upstream = $args[1]
$Expose = if ($args.Length -gt 2) { $args[2] } else { 'serve' }

if (-not (Get-Command tailscale -ErrorAction SilentlyContinue)) {
  Write-Error 'tailscale CLI not found on PATH.'
  exit 127
}

$status = (& tailscale status --json) | ConvertFrom-Json
$dns = ($status.Self.DNSName).TrimEnd('.')
if (-not $dns) { Write-Error 'Failed to resolve MagicDNS name. Try `tailscale up` first.'; exit 3 }

$tsCertDir = if ($env:TEMP) { $env:TEMP } else { '.' }
& tailscale cert --cert-file="$tsCertDir\$dns.crt" --key-file="$tsCertDir\$dns.key" $dns *> $null

# A hard-killed previous run leaves its rule behind in the system daemon, and funnel is a
# per-port flag — a tailnet-only run must not inherit that port's public exposure.
if ($Expose -ne 'funnel') {
  try { & tailscale funnel --https=$Port off *> $null } catch { }
}

if ($Expose -eq 'funnel') {
  Write-Warning 'This URL is publicly reachable on the internet — pair with auth.'
  & tailscale funnel --bg --https=$Port --set-path=/ $Upstream
} else {
  & tailscale serve --bg --https=$Port --set-path=/ $Upstream
}

Write-Output "TAP_TAILSCALE_HOSTNAME=$dns"
Write-Output "TAP_TAILSCALE_READY $dns ($Expose)"

try {
  while ($true) { Start-Sleep -Seconds 3600 }
} finally {
  & tailscale serve --https=$Port --set-path=/ off *> $null
  & tailscale funnel --https=$Port off *> $null
}
""";

    private static string WindowsSystemScript() => WindowsSystemBody;

    /// <summary>
    /// Pick the script interpreter for the host OS. macOS/Linux use bash; Windows uses PowerShell.
    /// Returned tuple is (command, fileExtension) — the extension is informational; the actual
    /// script path is generated per-resource by the lifecycle hook.
    /// </summary>
    public static (string Command, string Extension) ScriptInterpreter() =>
        OperatingSystem.IsWindows()
            ? ("powershell.exe", ".ps1")
            : ("bash", ".sh");
}
