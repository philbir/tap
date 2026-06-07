#!/usr/bin/env bash
# Build Tap NuGets and drop them into a local file-system feed so a sibling repo
# (e.g. Antoniq's AppHost) can consume the fix without going through nuget.org.
#
# Usage:
#   scripts/pack-local.sh                                    # default version + feed
#   scripts/pack-local.sh 0.5.0-local                        # custom version
#   scripts/pack-local.sh 0.5.0-local /path/to/nuget-feed    # custom version + feed
#
# Consumer setup (in the sibling repo):
#   1. Create or edit NuGet.config:
#        <packageSources>
#          <add key="tap-local" value="/Users/p7e/code/nuget-source" />
#        </packageSources>
#   2. dotnet add package Tap.Aspire.Hosting --version 0.5.0-local --source tap-local
#   3. dotnet nuget locals http-cache --clear   # if you republish the same version
#
# Notes:
#   - Local feed is a flat directory of .nupkg files; `dotnet pack -o <feed>` is
#     enough — no `nuget push` needed.
#   - Both Tap.Aspire.Hosting AND Tap.Internals (the transitive Tap.Core package)
#     land in the feed, since the former has a ProjectReference to the latter.
#   - If you republish the same version, run the `nuget locals http-cache --clear`
#     line above on the consumer side; NuGet caches by version string.

set -euo pipefail

VERSION="${1:-0.5.0-local}"
FEED="${2:-/Users/p7e/code/nuget-source}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

mkdir -p "$FEED"

echo "→ Packing Tap @ $VERSION into $FEED"
dotnet pack "$REPO_ROOT/src/Tap.Core/Tap.Core.csproj" \
    -c Release -p:Version="$VERSION" -o "$FEED" --nologo
dotnet pack "$REPO_ROOT/src/Tap.Hosting/Tap.Hosting.csproj" \
    -c Release -p:Version="$VERSION" -o "$FEED" --nologo

echo
echo "Done. Packages in $FEED:"
ls -1 "$FEED" | grep -E "^(Tap\.Aspire\.Hosting|Tap\.Internals)\.${VERSION//./\\.}\.(nu|sn)pkg$" || true
echo
echo "Consume from a sibling repo with:"
echo "  dotnet add package Tap.Aspire.Hosting --version $VERSION --source $FEED"
