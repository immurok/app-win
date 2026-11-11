#!/bin/sh
# immurok WSL2 support — uninstaller, run as root inside the distro by the
# Windows client when the "Support WSL2" switch is turned off (and by the
# Windows uninstaller). Removes everything wsl-install.sh put in place;
# safe to run twice.
#
# Design: docs/superpowers/specs/2026-09-06-wsl2-support-design.md §5.2
set -u

LIB=/usr/local/lib/immurok
ETC=/etc/immurok
UNIT=/etc/systemd/system/immurok-wsl-bridge.service

[ "$(id -u)" = 0 ] || { echo "wsl-uninstall: must run as root" >&2; exit 1; }

# ---- PAM: drop our line by content, never restore the backup wholesale ------------
# (the user may have edited the file since we installed).
for svc in sudo sudo-i; do
    file=/etc/pam.d/$svc
    [ -f "$file" ] || continue
    if grep -qF "$LIB/pam_immurok.so" "$file"; then
        tmp="$file.immurok-new"
        grep -vF "$LIB/pam_immurok.so" "$file" > "$tmp"
        chown --reference="$file" "$tmp" 2>/dev/null || true
        chmod --reference="$file" "$tmp" 2>/dev/null || chmod 0644 "$tmp"
        mv -f "$tmp" "$file"
    fi
    rm -f "$file.immurok-bak"
done

# ---- bridge -------------------------------------------------------------------------
if [ -e "$UNIT" ]; then
    if [ -d /run/systemd/system ]; then
        systemctl disable --now immurok-wsl-bridge.service >/dev/null 2>&1 || true
    fi
    rm -f "$UNIT"
    [ -d /run/systemd/system ] && systemctl daemon-reload >/dev/null 2>&1
fi
pkill -x immurok-wsl-bri 2>/dev/null || true

# wsl.conf boot command: put the user's original value back.
CONF=/etc/wsl.conf
if [ -f "$CONF" ] && grep -qF "$ETC/boot.sh" "$CONF"; then
    orig=$(cat "$ETC/boot.orig" 2>/dev/null || true)
    awk -v orig="$orig" -v ours="$ETC/boot.sh" '
        index($0, ours) && /^[[:space:]]*command[[:space:]]*=/ {
            if (orig != "") print "command = " orig
            next
        }
        { print }
    ' "$CONF" > "$CONF.immurok-new"
    mv -f "$CONF.immurok-new" "$CONF"
    rm -f "$CONF.immurok-bak"
fi

# ---- ssh / profile ---------------------------------------------------------------------
rm -f /etc/ssh/ssh_config.d/90-immurok.conf /etc/profile.d/immurok.sh

# ---- files ---------------------------------------------------------------------------------
[ -L /usr/local/bin/imk ] && [ "$(readlink /usr/local/bin/imk)" = "$LIB/imk" ] && rm -f /usr/local/bin/imk
rm -rf "$ETC" /run/immurok
rm -f /var/log/immurok-wsl-bridge.log
# Last: this script lives in $LIB.
rm -rf "$LIB"
exit 0
