#!/usr/bin/env bash
# Local-dev convenience wrapper: publish the sidecar for the host triple and
# launch (or bundle) the Tauri shell. CI doesn't use this — it calls
# apps/tap-studio-desktop/scripts/compile-server.mjs directly per matrix entry
# and then runs tauri-action.
#
#   scripts/build-desktop.sh          # publish sidecar + yarn tauri build
#   scripts/build-desktop.sh --dev    # publish sidecar + yarn tauri dev
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESKTOP_DIR="$REPO_ROOT/apps/tap-studio-desktop"

DEV=false
if [[ "${1:-}" == "--dev" ]]; then
  DEV=true
fi

# Publish the .NET sidecar + stage wwwroot for the host triple.
# compile-server.mjs detects the triple from `rustc -vV` (preferred) or Node
# platform info and writes into apps/tap-studio-desktop/src-tauri/binaries/.
node "$DESKTOP_DIR/scripts/compile-server.mjs"

cd "$DESKTOP_DIR"
if [[ ! -d node_modules ]]; then
  yarn install
fi
if $DEV; then
  yarn tauri dev
else
  yarn tauri build
fi
