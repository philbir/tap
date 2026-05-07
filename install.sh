#!/bin/sh
# tap installer — downloads the latest self-contained release for the current
# platform and installs a launcher into ~/.local/bin (or $TAP_BIN_DIR).
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh
#
# Environment:
#   TAP_VERSION       Pin a specific version (defaults to latest GitHub release).
#   TAP_INSTALL_DIR   Where the unpacked release lives (default ~/.local/share/tap).
#   TAP_BIN_DIR       Where the `tap` launcher is created (default ~/.local/bin).

set -eu

REPO="philbir/tap"
INSTALL_DIR="${TAP_INSTALL_DIR:-$HOME/.local/share/tap}"
BIN_DIR="${TAP_BIN_DIR:-$HOME/.local/bin}"

uname_s=$(uname -s)
uname_m=$(uname -m)
case "$uname_s" in
  Darwin) os="osx" ;;
  Linux)  os="linux" ;;
  *) echo "tap: unsupported OS '$uname_s'. Try 'dotnet tool install -g Tap' instead." >&2; exit 1 ;;
esac
case "$uname_m" in
  x86_64|amd64)   arch="x64" ;;
  arm64|aarch64)  arch="arm64" ;;
  *) echo "tap: unsupported architecture '$uname_m'. Try 'dotnet tool install -g Tap' instead." >&2; exit 1 ;;
esac
rid="${os}-${arch}"

if [ -n "${TAP_VERSION:-}" ]; then
  version="$TAP_VERSION"
else
  version=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | grep -m1 '"tag_name":' \
    | cut -d'"' -f4)
fi
if [ -z "$version" ]; then
  echo "tap: could not resolve latest version. Set TAP_VERSION or check network." >&2
  exit 1
fi

asset="tap-${version}-${rid}.tar.gz"
asset_url="https://github.com/$REPO/releases/download/$version/$asset"
sums_url="https://github.com/$REPO/releases/download/$version/SHA256SUMS"

echo "tap: installing $version for $rid"

tmp=$(mktemp -d 2>/dev/null || mktemp -d -t tap-install)
trap 'rm -rf "$tmp"' EXIT

echo "tap: downloading $asset"
if ! curl -fsSL "$asset_url" -o "$tmp/$asset"; then
  echo "tap: failed to download $asset_url" >&2
  exit 1
fi

# Verify checksum if SHA256SUMS is published.
if curl -fsSL "$sums_url" -o "$tmp/SHA256SUMS" 2>/dev/null; then
  expected=$(grep " $asset\$" "$tmp/SHA256SUMS" | awk '{print $1}' || true)
  if [ -n "$expected" ]; then
    if command -v sha256sum >/dev/null 2>&1; then
      actual=$(sha256sum "$tmp/$asset" | awk '{print $1}')
    else
      actual=$(shasum -a 256 "$tmp/$asset" | awk '{print $1}')
    fi
    if [ "$expected" != "$actual" ]; then
      echo "tap: checksum mismatch (expected $expected, got $actual)" >&2
      exit 1
    fi
    echo "tap: checksum OK"
  fi
else
  echo "tap: SHA256SUMS not available, skipping checksum verification"
fi

echo "tap: extracting"
( cd "$tmp" && tar -xzf "$asset" )
src_dir="$tmp/tap-${version}-${rid}"
if [ ! -d "$src_dir" ]; then
  echo "tap: archive layout unexpected (no $src_dir)" >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR" "$BIN_DIR"
# Wipe the prior install so we never leave stale framework files behind.
rm -rf "$INSTALL_DIR"/*
cp -R "$src_dir/." "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/tap"

# Prefer a launcher shim over a symlink: ASP.NET Core resolves wwwroot from the
# binary's actual directory, and a tiny shim avoids any symlink-resolution edge
# cases across macOS/Linux.
launcher="$BIN_DIR/tap"
cat > "$launcher" <<EOF
#!/bin/sh
exec "$INSTALL_DIR/tap" "\$@"
EOF
chmod +x "$launcher"

echo
echo "tap: installed $version"
echo "  app:      $INSTALL_DIR"
echo "  launcher: $launcher"
echo

case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *)
    echo "tap: warning — $BIN_DIR is not on your PATH."
    echo "     Add this to your shell profile and reopen the terminal:"
    echo "       export PATH=\"$BIN_DIR:\$PATH\""
    ;;
esac

if ! command -v cloudflared >/dev/null 2>&1; then
  echo
  echo "tap: cloudflared is not on PATH. Cloudflare-tunnel features need it."
  echo "     Install with: brew install cloudflared  (or)  tap install-cloudflared"
fi
