# tap installer for Windows — installs the `tap` CLI as a .NET global tool.
#
# Usage:
#   irm https://raw.githubusercontent.com/philbir/tap/main/install.ps1 | iex
#
# Environment / parameters:
#   $env:TAP_VERSION   Pin a specific version (e.g. "0.2.3"). Defaults to latest.
#
# Notes:
#   We deliberately do not ship a self-contained Windows binary; the .NET tool
#   install is the supported Windows path. macOS / Linux users use install.sh.

$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error @"
tap: 'dotnet' is not on PATH. Install the .NET 10 SDK first:
  https://dotnet.microsoft.com/download/dotnet/10.0
Then re-run this script.
"@
    exit 1
}

$versionArgs = @()
if ($env:TAP_VERSION) {
    $versionArgs = @("--version", $env:TAP_VERSION)
    Write-Host "tap: installing pinned version $($env:TAP_VERSION)"
} else {
    Write-Host "tap: installing latest version"
}

# Use update if Tap is already installed, install otherwise.
$listed = & dotnet tool list -g 2>$null | Select-String -Pattern '(?i)^\s*tap\s'
$verb = if ($listed) { "update" } else { "install" }

& dotnet tool $verb -g Tap @versionArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "tap: 'dotnet tool $verb -g Tap' failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$toolsDir = Join-Path $env:USERPROFILE ".dotnet\tools"
$onPath = ($env:Path -split ';') -contains $toolsDir
if (-not $onPath) {
    Write-Host ""
    Write-Host "tap: warning — '$toolsDir' is not on your PATH."
    Write-Host "     Add it via:"
    Write-Host "       [Environment]::SetEnvironmentVariable('Path', `$env:Path + ';$toolsDir', 'User')"
    Write-Host "     Then open a new terminal."
}

if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "tap: cloudflared is not on PATH. Cloudflare-tunnel features need it."
    Write-Host "     Install with: winget install Cloudflare.cloudflared  (or)  tap install-cloudflared"
}

Write-Host ""
Write-Host "tap: done. Run 'tap --help' to get started."
