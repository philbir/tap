#!/bin/sh
# tap installer — downloads the latest self-contained release for the current
# platform and installs a launcher into ~/.local/bin (or $TAP_BIN_DIR).
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh
#
# Environment:
#   TAP_VERSION        Pin a specific version (defaults to latest GitHub release).
#   TAP_INSTALL_DIR    Where the unpacked release lives (default ~/.local/share/tap).
#   TAP_BIN_DIR        Where the `tap` launcher is created (default ~/.local/bin).
#   TAP_SKIP_CHECKSUM  Set to 1 to install without verifying SHA256SUMS.

set -eu

REPO="philbir/tap"
INSTALL_DIR="${TAP_INSTALL_DIR:-$HOME/.local/share/tap}"
BIN_DIR="${TAP_BIN_DIR:-$HOME/.local/bin}"

# INSTALL_DIR comes from the environment and its contents get wiped below.
# Reject anything that isn't a plausible install path so an unset HOME or a
# stray TAP_INSTALL_DIR=/ can't turn the upgrade path into `rm -rf /*`.
case "$INSTALL_DIR" in
  *..*)
    echo "tap: refusing TAP_INSTALL_DIR='$INSTALL_DIR' — must not contain '..'." >&2
    exit 1 ;;
esac
# Require an absolute path with at least two segments: `/`, `/usr` and `/home`
# are never install roots, but `/opt/tap` and ~/.local/share/tap are.
case "${INSTALL_DIR%/}" in
  /*/*) ;;
  *)
    echo "tap: refusing TAP_INSTALL_DIR='$INSTALL_DIR' — too close to the filesystem root." >&2
    exit 1 ;;
esac
if [ "${INSTALL_DIR%/}" = "${HOME:-}" ]; then
  echo "tap: refusing to install directly into your home directory." >&2
  exit 1
fi

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

# Verify the download against the release's SHA256SUMS. This fails closed: a
# missing manifest and a tampered manifest look identical from here, so both
# abort rather than silently installing an unverified binary.
if [ "${TAP_SKIP_CHECKSUM:-0}" = "1" ]; then
  echo "tap: TAP_SKIP_CHECKSUM=1 — installing without checksum verification"
else
  if ! curl -fsSL "$sums_url" -o "$tmp/SHA256SUMS"; then
    echo "tap: could not download SHA256SUMS from $sums_url" >&2
    echo "     Refusing to install an unverified binary." >&2
    echo "     Set TAP_SKIP_CHECKSUM=1 to override." >&2
    exit 1
  fi
  # Exact filename match on field 2 — a substring/regex match would let an
  # unrelated entry vouch for this asset.
  expected=$(awk -v f="$asset" '$2 == f { print $1 }' "$tmp/SHA256SUMS")
  if [ -z "$expected" ]; then
    echo "tap: $asset has no entry in SHA256SUMS." >&2
    echo "     Refusing to install an unverified binary." >&2
    echo "     Set TAP_SKIP_CHECKSUM=1 to override." >&2
    exit 1
  fi
  if command -v sha256sum >/dev/null 2>&1; then
    actual=$(sha256sum "$tmp/$asset" | awk '{print $1}')
  elif command -v shasum >/dev/null 2>&1; then
    actual=$(shasum -a 256 "$tmp/$asset" | awk '{print $1}')
  else
    echo "tap: neither sha256sum nor shasum is available — cannot verify $asset." >&2
    echo "     Install coreutils, or set TAP_SKIP_CHECKSUM=1 to override." >&2
    exit 1
  fi
  if [ "$expected" != "$actual" ]; then
    echo "tap: checksum mismatch (expected $expected, got $actual)" >&2
    exit 1
  fi
  echo "tap: checksum OK"
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
# Safe because INSTALL_DIR was validated as a plausible install path above.
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
