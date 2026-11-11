#!/bin/sh
# Build the WSL2 payload for the host architecture:
#
#   usr/local/lib/immurok/VERSION
#   usr/local/lib/immurok/wsl-install.sh
#   usr/local/lib/immurok/wsl-uninstall.sh
#   usr/local/lib/immurok/<arch>/{immurok-wsl-bridge,imk,pam_immurok.so}
#
# The Windows client streams this tar into a distro when the user turns on
# "WSL2 support". A release payload carries both architectures: the release
# workflow runs this script once per architecture and joins the results with
# merge-payload.sh. A single-architecture payload is fine for local testing
# on that architecture.
#
#   wsl/build-payload.sh [--linux-src DIR] [--out FILE]
#
# Two of the three binaries are not ours to build from this tree: `imk` and
# `pam_immurok.so` are the Linux app's, run unmodified inside WSL. --linux-src
# points at a checkout of it (github.com/immurok/app-linux-rs); the default is
# the sibling directory, which is where it sits in the development tree. The
# bridge is this repo's own (wsl/bridge).
#
# Needs: cargo with the <arch>-unknown-linux-musl target, musl-tools, a C
# compiler, binutils and the libpam headers.
#
# The bridge and imk are static musl binaries, so they run on any distro.
# pam_immurok.so cannot be static (sudo dlopens it); it links only libpam
# and glibc, so it must be built on the oldest glibc we intend to support.
# The release workflow builds in debian:12 (glibc 2.36), the same baseline
# as the native Linux packages.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ARCH=$(uname -m)
LINUX_SRC="$HERE/../../app-linux-rs"
OUT="$HERE/../packaging/wsl/immurok-wsl-payload.tar.gz"
while [ $# -gt 0 ]; do
    case "$1" in
        --linux-src) LINUX_SRC="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        *) echo "build-payload: unknown argument $1" >&2; exit 64 ;;
    esac
done
[ -f "$LINUX_SRC/pam/pam_immurok.c" ] && [ -f "$LINUX_SRC/crates/immurok-cli/Cargo.toml" ] || {
    echo "build-payload: $LINUX_SRC is not an app-linux-rs checkout (pass --linux-src)" >&2
    exit 1
}
LINUX_SRC=$(cd "$LINUX_SRC" && pwd)

TARGET="$ARCH-unknown-linux-musl"
# One target directory for both builds. Override CARGO_TARGET_DIR when the
# sources sit on a Windows mount (slow, and no exec bit).
: "${CARGO_TARGET_DIR:=$HERE/bridge/target}"
export CARGO_TARGET_DIR

cargo build --release --locked --target "$TARGET" --manifest-path "$HERE/bridge/Cargo.toml"
cargo build --release --locked --target "$TARGET" --manifest-path "$LINUX_SRC/Cargo.toml" -p immurok-cli --bin imk

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
LIB="$STAGE/usr/local/lib/immurok"
mkdir -p "$LIB/$ARCH"

install -m 0755 "$CARGO_TARGET_DIR/$TARGET/release/immurok-wsl-bridge" "$LIB/$ARCH/"
install -m 0755 "$CARGO_TARGET_DIR/$TARGET/release/imk" "$LIB/$ARCH/"
"${CC:-cc}" -fPIC -shared -O2 -Wall -Wextra -o "$LIB/$ARCH/pam_immurok.so" "$LINUX_SRC/pam/pam_immurok.c" -lpam
chmod 0644 "$LIB/$ARCH/pam_immurok.so"

install -m 0755 "$HERE/wsl-install.sh" "$HERE/wsl-uninstall.sh" "$LIB/"
# Scripts must reach the distro with LF endings even from a CRLF checkout.
sed -i 's/\r$//' "$LIB/wsl-install.sh" "$LIB/wsl-uninstall.sh"

# VERSION names what is inside: our bridge, and the Linux app version the
# other two binaries were built from. The pattern stops at the closing quote
# so a CRLF checkout of Cargo.toml does not leak a carriage return into it.
BRIDGE_VERSION=$(sed -n 's/^version = "\([^"]*\)".*/\1/p' "$HERE/bridge/Cargo.toml" | head -1)
LINUX_VERSION=$(sed -n 's/^version = "\([^"]*\)".*/\1/p' "$LINUX_SRC/crates/immurok-cli/Cargo.toml" | head -1)
[ -n "$BRIDGE_VERSION" ] && [ -n "$LINUX_VERSION" ] || { echo "build-payload: could not read versions" >&2; exit 1; }
printf 'bridge %s\nlinux %s\n' "$BRIDGE_VERSION" "$LINUX_VERSION" > "$LIB/VERSION"

# ---- sanity: what we are about to ship actually runs and is what we think ----
sh -n "$LIB/wsl-install.sh"
sh -n "$LIB/wsl-uninstall.sh"
"$LIB/$ARCH/immurok-wsl-bridge" --version | grep -qx "immurok-wsl-bridge $BRIDGE_VERSION" \
    || { echo "build-payload: bridge does not report version $BRIDGE_VERSION" >&2; exit 1; }
"$LIB/$ARCH/imk" version | grep -q "$LINUX_VERSION" \
    || { echo "build-payload: imk does not report version $LINUX_VERSION" >&2; exit 1; }
# Static means static: a dynamic interpreter here would tie the binary to
# the build machine's libc.
for b in immurok-wsl-bridge imk; do
    if readelf -l "$LIB/$ARCH/$b" 2>/dev/null | grep -q 'Requesting program interpreter'; then
        echo "build-payload: $b is dynamically linked" >&2
        exit 1
    fi
done
# The PAM module may depend on libpam and glibc (plus what libpam pulls in)
# and nothing else.
if readelf -d "$LIB/$ARCH/pam_immurok.so" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p' \
    | grep -Ev '^(libpam\.so\.0|libc\.so\.6)$' | grep -q .; then
    echo "build-payload: pam_immurok.so has unexpected dependencies:" >&2
    readelf -d "$LIB/$ARCH/pam_immurok.so" | grep NEEDED >&2
    exit 1
fi

mkdir -p "$(dirname "$OUT")"
# Leaf directory only: unpacking must not touch the mode or mtime of /usr.
tar czf "$OUT" --owner=0 --group=0 --numeric-owner -C "$STAGE" usr/local/lib/immurok
echo "payload: $OUT"
tar tzvf "$OUT"
