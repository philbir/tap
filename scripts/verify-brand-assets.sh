#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"

required=(
  assets/tap-platform-icon.svg
  assets/tap-platform-icon-128.png
  assets/tap-platform-icon-192.png
  assets/tap-platform-icon-512.png
  assets/tap-platform-apple-touch-icon.png
  assets/tap-studio-icon.svg
  assets/tap-studio-icon-128.png
  assets/tap-studio-icon-192.png
  assets/tap-studio-icon-512.png
  assets/tap-studio-apple-touch-icon.png
  assets/tap-tunnels-icon.svg
  assets/tap-tunnels-icon-128.png
  assets/tap-tunnels-icon-192.png
  assets/tap-tunnels-icon-512.png
  assets/tap-tunnels-apple-touch-icon.png
  docs-site/public/site.webmanifest
  src/ui-inspector/public/site.webmanifest
  src/ui-studio/public/site.webmanifest
  src/desktop/src-tauri/icons/icon.icns
  src/desktop/src-tauri/icons/icon.ico
  src/desktop/src-tauri/icons/icon.png
)

for file in "${required[@]}"; do
  if [[ ! -s "$file" ]]; then
    echo "Missing published brand asset: $file" >&2
    exit 1
  fi
done

same_as() {
  local canonical=$1
  shift
  for alias in "$@"; do
    if ! cmp -s "$canonical" "$alias"; then
      echo "Stale brand alias: $alias must match $canonical" >&2
      exit 1
    fi
  done
}

same_as assets/tap-platform-icon.svg \
  assets/tap-favicon.svg \
  assets/tap-mark.svg \
  assets/tap-logo.svg \
  docs-site/public/tap-platform-icon.svg \
  docs-site/public/tap-favicon.svg \
  docs-site/public/tap-mark.svg \
  docs-site/public/tap-logo.svg

same_as assets/tap-platform-icon-32.png \
  docs-site/public/tap-platform-icon-32.png

same_as assets/tap-platform-icon-192.png \
  docs-site/public/tap-platform-icon-192.png

same_as assets/tap-platform-icon-512.png \
  docs-site/public/tap-platform-icon-512.png

same_as assets/tap-platform-apple-touch-icon.png \
  docs-site/public/tap-platform-apple-touch-icon.png

same_as assets/tap-studio-icon.svg \
  docs-site/public/tap-studio-icon.svg \
  src/ui-studio/public/tap-studio-icon.svg \
  src/desktop/src/tap-studio-icon.svg

same_as assets/tap-studio-icon-32.png \
  src/ui-studio/public/tap-studio-icon-32.png

same_as assets/tap-studio-icon-192.png \
  src/ui-studio/public/tap-studio-icon-192.png

same_as assets/tap-studio-icon-512.png \
  src/ui-studio/public/tap-studio-icon-512.png

same_as assets/tap-studio-apple-touch-icon.png \
  src/ui-studio/public/apple-touch-icon.png

same_as assets/tap-tunnels-icon.svg \
  docs-site/public/tap-tunnels-icon.svg \
  src/ui-inspector/public/tap-tunnels-icon.svg \
  src/ui-inspector/public/tap-favicon.svg \
  src/ui-inspector/public/tap-mark.svg \
  src/ui-inspector/public/tap-logo.svg \
  src/ui-inspector/public/icon.svg

same_as assets/tap-tunnels-icon-32.png \
  src/ui-inspector/public/tap-tunnels-icon-32.png

same_as assets/tap-tunnels-icon-192.png \
  src/ui-inspector/public/tap-tunnels-icon-192.png

same_as assets/tap-tunnels-icon-512.png \
  src/ui-inspector/public/tap-tunnels-icon-512.png

same_as assets/tap-tunnels-apple-touch-icon.png \
  src/ui-inspector/public/apple-touch-icon.png

echo "Published brand assets are present and synchronized."
