#!/usr/bin/env bash
# Convenience wrapper: launch the Studio UI dev server pointed at a locally-running
# Tap.Studio backend. Set STUDIO_API_URL to override the default discovery, otherwise
# we ask the Aspire CLI for the active studio-api URL.
set -euo pipefail
cd "$(dirname "$0")/.."

api_url="${STUDIO_API_URL:-}"

if [[ -z "$api_url" ]]; then
  # Best-effort discovery from a running Studio.AppHost via aspire describe.
  if command -v aspire >/dev/null && command -v python3 >/dev/null; then
    discovered=$(aspire describe \
        --apphost samples/Studio.AppHost/Studio.AppHost.csproj \
        --non-interactive --format Json 2>/dev/null \
      | python3 -c 'import json,sys
try:
  d = json.load(sys.stdin)
  s = next(r for r in d["resources"] if r["name"].startswith("studio-api") and r.get("state") == "Running")
  print(s["urls"][0]["url"])
except Exception:
  pass' 2>/dev/null || true)
    api_url="$discovered"
  fi
fi

if [[ -z "$api_url" ]]; then
  api_url="http://localhost:5298"
fi

export VITE_STUDIO_API_URL="$api_url"
echo "[studio-ui-dev] Proxying /api -> $VITE_STUDIO_API_URL"
exec yarn --cwd src/ui-studio dev
