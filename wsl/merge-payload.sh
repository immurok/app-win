#!/bin/sh
# Join per-architecture WSL payloads (from build-payload.sh) into the one
# release payload the Windows installer ships:
#
#   wsl/merge-payload.sh --out packaging/wsl/immurok-wsl-payload.tar.gz \
#       immurok-wsl-payload-x86_64.tar.gz immurok-wsl-payload-aarch64.tar.gz
#
# Refuses inputs that disagree on VERSION or on the install scripts — they
# were built from different sources — and a result that is missing either
# architecture.
set -eu

OUT=""
INPUTS=""
while [ $# -gt 0 ]; do
    case "$1" in
        --out) OUT="$2"; shift 2 ;;
        -*) echo "merge-payload: unknown argument $1" >&2; exit 64 ;;
        *) INPUTS="$INPUTS $1"; shift ;;
    esac
done
[ -n "$OUT" ] && [ -n "$INPUTS" ] || { echo "Usage: merge-payload.sh --out FILE PAYLOAD..." >&2; exit 64; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/stage"
LIB="$STAGE/usr/local/lib/immurok"
mkdir -p "$STAGE"

n=0
for f in $INPUTS; do
    n=$((n + 1))
    one="$WORK/in-$n"
    mkdir -p "$one"
    tar xzf "$f" -C "$one"
    src="$one/usr/local/lib/immurok"
    [ -f "$src/VERSION" ] || { echo "merge-payload: $f is not a WSL payload" >&2; exit 1; }
    if [ -d "$LIB" ]; then
        for common in VERSION wsl-install.sh wsl-uninstall.sh; do
            cmp -s "$src/$common" "$LIB/$common" \
                || { echo "merge-payload: $f disagrees with the other inputs on $common" >&2; exit 1; }
        done
        for d in "$src"/*/; do
            arch=$(basename "$d")
            [ ! -e "$LIB/$arch" ] || { echo "merge-payload: architecture $arch appears twice" >&2; exit 1; }
            cp -a "$d" "$LIB/$arch"
        done
    else
        mkdir -p "$(dirname "$LIB")"
        cp -a "$src" "$LIB"
    fi
done

for arch in x86_64 aarch64; do
    for b in immurok-wsl-bridge imk pam_immurok.so; do
        [ -f "$LIB/$arch/$b" ] || { echo "merge-payload: result is missing $arch/$b" >&2; exit 1; }
    done
done

mkdir -p "$(dirname "$OUT")"
tar czf "$OUT" --owner=0 --group=0 --numeric-owner -C "$STAGE" usr/local/lib/immurok
echo "payload: $OUT ($(paste -sd, "$LIB/VERSION"))"
tar tzvf "$OUT"
