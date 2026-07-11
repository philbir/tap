using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Tap.Core.Cloudflared;

public enum CloudflaredAvailability
{
    OnPath,
    Installed,        // we just installed it
    NotInstalled,
}

/// <summary>
/// Probes whether cloudflared is on PATH and, if not, attempts to install it via the host
/// platform's package manager (brew on macOS, winget on Windows, GitHub release on Linux).
/// </summary>
public sealed class CloudflaredInstaller(ILogger<CloudflaredInstaller> logger)
{
    public bool IsOnPath()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cloudflared", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            if (!p.WaitForExit(5_000)) { p.Kill(true); return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Idempotent install. If <paramref name="autoInstall"/> is false and cloudflared is missing,
    /// throws with a clear instruction.
    /// </summary>
    public async Task<CloudflaredAvailability> EnsureAvailableAsync(bool autoInstall, CancellationToken ct)
    {
        if (IsOnPath()) return CloudflaredAvailability.OnPath;

        if (!autoInstall)
        {
            throw new InvalidOperationException(
                "cloudflared not found on PATH. Install it manually:\n"
                + "  macOS:   brew install cloudflared\n"
                + "  Windows: winget install --id Cloudflare.cloudflared\n"
                + "  Linux:   https://pkg.cloudflare.com/index.html\n"
                + "or pass --auto-install / configure auto-install to install via the host package manager.");
        }

        logger.LogInformation("cloudflared not found on PATH — attempting auto-install.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await RunAsync("brew", "install cloudflared", ct);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await RunAsync("winget", "install --id Cloudflare.cloudflared --accept-source-agreements --accept-package-agreements", ct);
        }
        else
        {
            // Linux — try the official apt/yum repo first; fall back to direct binary download.
            await InstallLinuxAsync(ct);
        }

        if (!IsOnPath())
        {
            throw new InvalidOperationException(
                "cloudflared install reported success but the binary is still not on PATH. Open a new shell and retry.");
        }
        logger.LogInformation("cloudflared installed.");
        return CloudflaredAvailability.Installed;
    }

    private async Task InstallLinuxAsync(CancellationToken ct)
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture {RuntimeInformation.ProcessArchitecture}."),
        };
        var url = $"https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-{arch}";
        var dest = "/usr/local/bin/cloudflared";

        logger.LogInformation("Downloading {Url} -> {Dest}", url, dest);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        await using (var s = await http.GetStreamAsync(url, ct))
        await using (var fs = File.Create(dest))
        {
            await s.CopyToAsync(fs, ct);
        }
        await RunAsync("chmod", $"+x {dest}", ct);
    }

    private async Task RunAsync(string file, string args, CancellationToken ct)
    {
        logger.LogInformation("$ {File} {Args}", file, args);
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start {file}.");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"{file} {args} exited {p.ExitCode}: {err.Trim()}");
        }
    }
}
