#!/bin/sh
# immurok WSL2 support — installer, run as root inside the distro by the
# Windows client after it unpacked the payload tar into /:
#
#   wsl.exe -d <distro> -u root --exec /bin/sh -c \
#     'tar xzf - -C / && /usr/local/lib/immurok/wsl-install.sh --distro <name> --ssh-agent on'
#
# The token arrives in $IMK_WSL_TOKEN (passed through WSLENV, so it never
# shows up on a Windows command line); --token overrides it for manual use.
#
# Every step is idempotent: the client re-runs this after an immurok upgrade
# and when the user presses "retry". Undo with wsl-uninstall.sh.
#
# Design: docs/superpowers/specs/2026-09-06-wsl2-support-design.md §5.1
set -eu

LIB=/usr/local/lib/immurok
ETC=/etc/immurok
PAM_LINE="auth        sufficient    $LIB/pam_immurok.so"
UNIT=/etc/systemd/system/immurok-wsl-bridge.service
SSH_CONF=/etc/ssh/ssh_config.d/90-immurok.conf
PROFILE=/etc/profile.d/immurok.sh
BOOT_BEGIN="# >>> immurok-wsl-bridge >>>"
BOOT_END="# <<< immurok-wsl-bridge <<<"

TOKEN="${IMK_WSL_TOKEN:-}"
DISTRO=""
SSH_AGENT=on
while [ $# -gt 0 ]; do
    case "$1" in
        --token) TOKEN="$2"; shift 2 ;;
        --distro) DISTRO="$2"; shift 2 ;;
        --ssh-agent) SSH_AGENT="$2"; shift 2 ;;
        *) echo "wsl-install: unknown argument $1" >&2; exit 64 ;;
    esac
done

fail() { echo "wsl-install: $*" >&2; exit 1; }
warn() { echo "wsl-install: warning: $*" >&2; WARNINGS=$((WARNINGS + 1)); }
WARNINGS=0

[ "$(id -u)" = 0 ] || fail "must run as root"
case "$TOKEN" in
    *[!0-9a-f]*|"") fail "token (IMK_WSL_TOKEN or --token) must be 64 lowercase hex characters" ;;
esac
[ "${#TOKEN}" = 64 ] || fail "token (IMK_WSL_TOKEN or --token) must be 64 lowercase hex characters"
grep -qi microsoft /proc/version 2>/dev/null || fail "not a WSL kernel"
[ -e /dev/vsock ] || fail "/dev/vsock missing (WSL1 is not supported)"

if [ -e /usr/lib/systemd/system/immurok-daemon.service ] || [ -e /lib/systemd/system/immurok-daemon.service ] \
    || [ -e /etc/systemd/system/immurok-daemon.service ] || [ -e /usr/bin/immurok-daemon ] \
    || [ -e /usr/local/bin/immurok-daemon ]; then
    fail "the native immurok Linux package is installed here; remove it first (both want /run/immurok)"
fi

# ---- binaries -------------------------------------------------------------
ARCH=$(uname -m)
[ -d "$LIB/$ARCH" ] || fail "no payload for architecture $ARCH"
for f in immurok-wsl-bridge imk pam_immurok.so; do
    [ -f "$LIB/$ARCH/$f" ] || fail "payload is missing $ARCH/$f"
done
# Replace via rename: the running bridge and a sudo that has the PAM module
# mapped keep their old inode.
install -m 0755 "$LIB/$ARCH/immurok-wsl-bridge" "$LIB/immurok-wsl-bridge.new"
mv -f "$LIB/immurok-wsl-bridge.new" "$LIB/immurok-wsl-bridge"
install -m 0755 "$LIB/$ARCH/imk" "$LIB/imk.new"
mv -f "$LIB/imk.new" "$LIB/imk"
install -m 0644 "$LIB/$ARCH/pam_immurok.so" "$LIB/pam_immurok.so.new"
mv -f "$LIB/pam_immurok.so.new" "$LIB/pam_immurok.so"
chown -R root:root "$LIB"
chmod 0755 "$LIB" "$LIB/wsl-install.sh" "$LIB/wsl-uninstall.sh"
ln -sfn "$LIB/imk" /usr/local/bin/imk

# ---- token / distro name ----------------------------------------------------
mkdir -p "$ETC"
chmod 0755 "$ETC"
( umask 077; printf '%s\n' "$TOKEN" > "$ETC/token.new" )
mv -f "$ETC/token.new" "$ETC/token"
[ -n "$DISTRO" ] && printf '%s\n' "$DISTRO" > "$ETC/distro"

# ---- PAM: sudo ----------------------------------------------------------------
# `sufficient` in front of everything else: a touch passes, anything else
# (no device, bridge down, key press) falls through to the password.
add_pam() {
    svc=$1
    file=/etc/pam.d/$svc
    [ -f "$file" ] || return 1
    grep -qF "$LIB/pam_immurok.so" "$file" && return 0
    [ -e "$file.immurok-bak" ] || cp -p "$file" "$file.immurok-bak"
    tmp="$file.immurok-new"
    awk -v line="$PAM_LINE" '
        !done && /^[[:space:]]*(auth|@include)[[:space:]]/ { print line; done = 1 }
        { print }
        END { if (!done) print line }
    ' "$file" > "$tmp"
    chown --reference="$file" "$tmp" 2>/dev/null || true
    chmod --reference="$file" "$tmp" 2>/dev/null || chmod 0644 "$tmp"
    mv -f "$tmp" "$file"
}
add_pam sudo || warn "/etc/pam.d/sudo not found: sudo will keep asking for the password"
add_pam sudo-i || true

# ---- bridge autostart -----------------------------------------------------------
start_now() {
    # boot.command only runs at the next VM start; bring the bridge up now too.
    if ! pgrep -x immurok-wsl-bri >/dev/null 2>&1; then
        setsid "$LIB/immurok-wsl-bridge" >>/var/log/immurok-wsl-bridge.log 2>&1 </dev/null &
    fi
}

if [ -d /run/systemd/system ]; then
    cat > "$UNIT" <<EOF
[Unit]
Description=immurok WSL2 bridge (forwards fingerprint auth to the Windows service)
Documentation=https://immurok.com

[Service]
Type=simple
ExecStart=$LIB/immurok-wsl-bridge
Restart=on-failure
RestartSec=2
# 3 = token missing, 4 = native daemon installed: restarting cannot fix either.
RestartPreventExitStatus=3 4

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    systemctl enable immurok-wsl-bridge.service >/dev/null 2>&1 || warn "systemctl enable failed"
    systemctl restart immurok-wsl-bridge.service || warn "systemctl restart failed"
else
    # No systemd in this distro: hook /etc/wsl.conf [boot] command. An
    # existing command is kept — boot.sh starts the bridge, then runs it.
    CONF=/etc/wsl.conf
    touch "$CONF"
    if ! grep -qF "$ETC/boot.sh" "$CONF"; then
        orig=$(awk '
            /^[[:space:]]*\[/ { inboot = ($0 ~ /^[[:space:]]*\[boot\]/) }
            inboot && /^[[:space:]]*command[[:space:]]*=/ { sub(/^[^=]*=[[:space:]]*/, ""); print; exit }
        ' "$CONF")
        printf '%s\n' "$orig" > "$ETC/boot.orig"
        cp -p "$CONF" "$CONF.immurok-bak"
        awk -v cmd="command = $ETC/boot.sh" '
            /^[[:space:]]*\[/ {
                if (inboot && !done) { print cmd; done = 1 }
                inboot = ($0 ~ /^[[:space:]]*\[boot\]/); seen = seen || inboot
            }
            inboot && /^[[:space:]]*command[[:space:]]*=/ { if (!done) { print cmd; done = 1 }; next }
            { print }
            END {
                if (!done) { if (!seen) print "[boot]"; print cmd }
            }
        ' "$CONF.immurok-bak" > "$CONF.immurok-new"
        mv -f "$CONF.immurok-new" "$CONF"
    fi
    cat > "$ETC/boot.sh" <<EOF
#!/bin/sh
$BOOT_BEGIN
setsid $LIB/immurok-wsl-bridge >>/var/log/immurok-wsl-bridge.log 2>&1 </dev/null &
$BOOT_END
orig=\$(cat $ETC/boot.orig 2>/dev/null || true)
[ -n "\$orig" ] && exec /bin/sh -c "\$orig"
exit 0
EOF
    chmod 0755 "$ETC/boot.sh"
    pkill -x immurok-wsl-bri 2>/dev/null || true
    start_now
fi

# ---- ssh ------------------------------------------------------------------------
# IdentityAgent covers every ssh / git invocation, including non-login shells
# and agents that never read a profile; a user's own ~/.ssh/config still wins.
# SSH_AUTH_SOCK is for tools that only look at the environment (ssh-add -l).
if [ "$SSH_AGENT" = on ]; then
    : > "$ETC/ssh-agent-enabled"
    if [ -d /etc/ssh/ssh_config.d ]; then
        cat > "$SSH_CONF" <<EOF
# immurok WSL2 support: sign with the SSH keys held in the immurok device.
Host *
    IdentityAgent /run/immurok/agent.sock
EOF
    else
        warn "/etc/ssh/ssh_config.d not found: only login shells get SSH_AUTH_SOCK"
    fi
else
    rm -f "$ETC/ssh-agent-enabled" "$SSH_CONF"
fi
if [ -d /etc/profile.d ]; then
    cat > "$PROFILE" <<'EOF'
# immurok WSL2 support
if [ -f /etc/immurok/ssh-agent-enabled ] && [ -S /run/immurok/agent.sock ]; then
    export SSH_AUTH_SOCK=/run/immurok/agent.sock
fi
EOF
fi

# ---- verify -----------------------------------------------------------------------
# Give the bridge a moment to create its sockets, then one round trip to the
# Windows service. The exit code is this distro's install result.
i=0
while [ ! -S /run/immurok/pam.sock ] && [ $i -lt 30 ]; do
    sleep 0.1
    i=$((i + 1))
done
"$LIB/immurok-wsl-bridge" --check
