#!/usr/bin/env bash
# Publish Tap.Studio as a self-contained single-file binary.
#
# Usage:
#   scripts/publish-studio.sh                      # host RID, ./artifacts/studio/<rid>
#   scripts/publish-studio.sh osx-arm64            # one RID
#   scripts/publish-studio.sh osx-arm64 osx-x64    # several RIDs
#   scripts/publish-studio.sh --all                # all supported RIDs
#
# The build target BuildStudioUi runs first (yarn install + yarn build) and
# copies ui-studio/dist into wwwroot, so the resulting binary serves the SPA
# from its own filesystem at runtime.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/src/Tap.Studio/Tap.Studio.csproj"
OUT_BASE="$REPO_ROOT/artifacts/studio"

ALL_RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64)

host_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os="osx" ;;
    Linux)  os="linux" ;;
    MINGW*|MSYS*|CYGWIN*) os="win" ;;
    *) echo "Unsupported host OS: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch="arm64" ;;
    x86_64|amd64)  arch="x64" ;;
    *) echo "Unsupported host arch: $(uname -m)" >&2; exit 1 ;;
  esac
  echo "${os}-${arch}"
}

if [[ "${1:-}" == "--all" ]]; then
  RIDS=("${ALL_RIDS[@]}")
elif [[ $# -gt 0 ]]; then
  RIDS=("$@")
else
  RIDS=("$(host_rid)")
fi

for rid in "${RIDS[@]}"; do
  out="$OUT_BASE/$rid"
  echo "==> Publishing Tap.Studio for $rid → $out"
  rm -rf "$out"
  dotnet publish "$CSPROJ" \
    --configuration Release \
    --runtime "$rid" \
    --output "$out" \
    -p:PublishSingleFile=true \
    -p:SelfContained=true
done

echo
echo "Done. Binaries:"
for rid in "${RIDS[@]}"; do
  out="$OUT_BASE/$rid"
  exe=$(ls "$out"/Tap.Studio* 2>/dev/null | head -1 || true)
  printf '  %-14s %s\n' "$rid" "${exe:-<missing>}"
done
