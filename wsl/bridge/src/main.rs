//! immurok-wsl-bridge — the WSL2 side of immurok for Windows.
//!
//! WSL2 is a Hyper-V VM and cannot see the host's Bluetooth adapter, so the
//! device stays with the Windows `ImmurokService`. This bridge runs as root
//! inside the distro, owns the two sockets the Linux `imk` and
//! `pam_immurok.so` already talk to, and forwards every connection to the
//! Windows service over a Hyper-V socket (AF_VSOCK to the host, CID 2):
//!
//!   /run/immurok/pam.sock   → vsock port 0x494D4B01  (daemon line protocol)
//!   /run/immurok/agent.sock → vsock port 0x494D4B02  (ssh-agent protocol)
//!
//! One vsock connection per Unix connection, bytes spliced both ways until
//! EOF. No long-lived connection and no session state, so `wsl --shutdown`,
//! a Windows service restart or toggling the feature need no reconnect
//! logic. The ssh-agent protocol passes through unaltered, with one
//! exception the service knows about: progress frames it adds for the
//! fingerprint prompt, which the bridge draws on the client's terminal and
//! takes out of the stream (see `hint`). There is no idle timeout (an agent
//! connection may sit silent for minutes).
//!
//! Every vsock connection starts with one preamble line:
//!
//!   IMK-WSL/1 token=<64hex> uid=<uid> distro=<name> pid=<pid> pids=<chain> comm=<comm> age=<secs> hints=1
//!
//! Only `token` is authorization (it keeps anything that is not this
//! root-owned bridge from talking to the service); the rest is for logs and
//! request classification on the Windows side.
//!
//! Design: docs/superpowers/specs/2026-09-06-wsl2-support-design.md

use std::fs;
use std::io::{Read, Write};
use std::mem;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::Path;
use std::process::ExitCode;
use std::sync::Arc;
use std::thread;

mod hint;
use hint::SignHint;

// The contract with the Linux `imk` and `pam_immurok.so` that run unmodified
// inside WSL: where they look for the daemon, and how long one request line
// may be. These mirror app-linux-rs (immurok-common `protocol.rs` /
// `socket_proto.rs`; pam_immurok.c hard-codes the same socket path) and must
// change together with it.
const SYSTEM_RUNTIME_DIR: &str = "/run/immurok";
const PAM_SOCKET_NAME: &str = "pam.sock";
const AGENT_SOCKET_NAME: &str = "agent.sock";
const MAX_REQUEST_BYTES: usize = 64 * 1024;

const CLI_PORT: u32 = 0x494D_4B01;
const SSH_PORT: u32 = 0x494D_4B02;
const TOKEN_FILE: &str = "/etc/immurok/token";
const DISTRO_FILE: &str = "/etc/immurok/distro";
/// Ancestor chain depth sent in `pids=` (self first).
const MAX_PID_CHAIN: usize = 12;

const EXIT_OK: u8 = 0;
const EXIT_FAILED: u8 = 1;
const EXIT_UNAVAILABLE: u8 = 2;
const EXIT_NO_TOKEN: u8 = 3;
const EXIT_NATIVE_DAEMON: u8 = 4;

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Cli,
    Ssh,
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        None => serve(),
        Some("--check") => check(),
        Some("--version") | Some("-V") => {
            println!("immurok-wsl-bridge {}", env!("CARGO_PKG_VERSION"));
            ExitCode::from(EXIT_OK)
        }
        _ => {
            eprintln!("Usage: immurok-wsl-bridge [--check | --version]");
            ExitCode::from(EXIT_FAILED)
        }
    }
}

// ── serve ─────────────────────────────────────────────────────

fn serve() -> ExitCode {
    if unsafe { libc::geteuid() } != 0 {
        eprintln!("immurok-wsl-bridge: must run as root");
        return ExitCode::from(EXIT_FAILED);
    }
    if let Some(path) = native_daemon_present() {
        eprintln!(
            "immurok-wsl-bridge: native immurok daemon found ({path}); both want {SYSTEM_RUNTIME_DIR}, refusing to start"
        );
        return ExitCode::from(EXIT_NATIVE_DAEMON);
    }
    let token = match read_token() {
        Some(t) => t,
        None => {
            eprintln!("immurok-wsl-bridge: {TOKEN_FILE} missing or malformed");
            return ExitCode::from(EXIT_NO_TOKEN);
        }
    };
    let distro = distro_name();

    // A writer that vanished must surface as EPIPE on write, not kill us.
    unsafe { libc::signal(libc::SIGPIPE, libc::SIG_IGN) };

    // root 0755 — what pam_immurok.so's socket_trust.h requires of the
    // directory before it will connect.
    let dir = Path::new(SYSTEM_RUNTIME_DIR);
    if let Err(e) = fs::create_dir_all(dir)
        .and_then(|_| fs::set_permissions(dir, fs::Permissions::from_mode(0o755)))
    {
        eprintln!("immurok-wsl-bridge: cannot prepare {SYSTEM_RUNTIME_DIR}: {e}");
        return ExitCode::from(EXIT_FAILED);
    }

    let mut handles = Vec::new();
    for (name, kind) in [(PAM_SOCKET_NAME, Kind::Cli), (AGENT_SOCKET_NAME, Kind::Ssh)] {
        let path = dir.join(name);
        let _ = fs::remove_file(&path);
        let listener = match UnixListener::bind(&path) {
            Ok(l) => l,
            Err(e) => {
                eprintln!("immurok-wsl-bridge: bind {}: {e}", path.display());
                return ExitCode::from(EXIT_FAILED);
            }
        };
        // Same layout as the hardened native daemon: world-connectable
        // socket inside a root-owned directory.
        if let Err(e) = fs::set_permissions(&path, fs::Permissions::from_mode(0o666)) {
            eprintln!("immurok-wsl-bridge: chmod {}: {e}", path.display());
            return ExitCode::from(EXIT_FAILED);
        }
        let token = token.clone();
        let distro = distro.clone();
        handles.push(thread::spawn(move || accept_loop(listener, kind, token, distro)));
    }
    eprintln!(
        "immurok-wsl-bridge {} listening in {SYSTEM_RUNTIME_DIR} (distro {distro})",
        env!("CARGO_PKG_VERSION")
    );
    for h in handles {
        let _ = h.join();
    }
    ExitCode::from(EXIT_FAILED)
}

fn accept_loop(listener: UnixListener, kind: Kind, token: String, distro: String) {
    for conn in listener.incoming() {
        match conn {
            Ok(stream) => {
                let token = token.clone();
                let distro = distro.clone();
                thread::spawn(move || handle(stream, kind, &token, &distro));
            }
            Err(e) => {
                eprintln!("immurok-wsl-bridge: accept: {e}");
                thread::sleep(std::time::Duration::from_millis(200));
            }
        }
    }
}

fn handle(mut client: UnixStream, kind: Kind, token: &str, distro: &str) {
    let (uid, pid) = peer_cred(client.as_raw_fd()).unwrap_or((u32::MAX, 0));

    // Line protocol only: look at the first chunk before forwarding, to
    // apply the one tier rule that still means something inside WSL.
    let mut first = Vec::new();
    if kind == Kind::Cli {
        let mut buf = vec![0u8; MAX_REQUEST_BYTES];
        match client.read(&mut buf) {
            Ok(0) | Err(_) => return,
            Ok(n) => first.extend_from_slice(&buf[..n]),
        }
        if !cli_request_allowed(&first, uid) {
            let _ = client.write_all(b"DENY:NOT_AUTHORIZED\n");
            return;
        }
    }

    let port = match kind {
        Kind::Cli => CLI_PORT,
        Kind::Ssh => SSH_PORT,
    };
    let host = match vsock_connect(port) {
        Ok(fd) => fd,
        Err(_) => {
            // Feature switched off or Windows service not running. Answer
            // at once so PAM falls through to the password instead of
            // waiting; an ssh client just moves on to its next key.
            if kind == Kind::Cli {
                let _ = client.write_all(b"ERROR:BRIDGE_UNAVAILABLE\n");
            }
            return;
        }
    };

    let mut head = preamble(token, uid, pid, distro).into_bytes();
    head.extend_from_slice(&first);
    if write_all_fd(host.as_raw_fd(), &head).is_err() {
        if kind == Kind::Cli {
            let _ = client.write_all(b"ERROR:BRIDGE_UNAVAILABLE\n");
        }
        return;
    }
    let hint = (kind == Kind::Ssh).then(|| SignHint::new(pid));
    splice(client, host, hint);
}

/// `AUTH:` is what a PAM stack sends, and sudo runs its PAM stack as root:
/// accept it from uid 0 only. Everything else passes — inside WSL every
/// account belongs to the same Windows user, so the native daemon's
/// "has an active logind session" tiering has nothing to distinguish.
fn cli_request_allowed(first_chunk: &[u8], uid: u32) -> bool {
    !first_chunk.starts_with(b"AUTH:") || uid == 0
}

/// Copy both directions until each side reaches EOF, propagating
/// half-closes. No idle timeout.
///
/// `hint` (agent connections only) is shown every chunk on its way through
/// so it can draw the fingerprint prompt on the client's terminal. Requests
/// go on exactly as read; from the responses it removes the service's
/// progress frames and nothing else.
fn splice(client: UnixStream, host: OwnedFd, hint: Option<Arc<SignHint>>) {
    let c = client.as_raw_fd();
    let h = host.as_raw_fd();
    let up_hint = hint.clone();
    let up = thread::spawn(move || {
        pump(c, h, |chunk, out| match &up_hint {
            Some(hint) => hint.client_bytes(chunk, out),
            None => out.extend_from_slice(chunk),
        });
        // This direction ending means the client hung up. If a prompt is
        // still on screen it was interrupted (Ctrl+C), not answered.
        if let Some(hint) = &up_hint {
            hint.client_gone();
        }
    });
    pump(h, c, |chunk, out| match &hint {
        Some(hint) => hint.host_bytes(chunk, out),
        None => out.extend_from_slice(chunk),
    });
    let _ = up.join();
    if let Some(hint) = &hint {
        hint.closed();
    }
    // `client` and `host` drop here, after both pumps are done with the fds.
}

/// `relay` decides what of each chunk read goes on to the other side by
/// appending it to its second argument — everything, for all but the
/// service-to-client half of an agent connection.
fn pump(from: RawFd, to: RawFd, mut relay: impl FnMut(&[u8], &mut Vec<u8>)) {
    let mut buf = [0u8; 16 * 1024];
    let mut out = Vec::with_capacity(buf.len());
    loop {
        let n = unsafe { libc::read(from, buf.as_mut_ptr().cast(), buf.len()) };
        if n < 0 && std::io::Error::last_os_error().kind() == std::io::ErrorKind::Interrupted {
            continue;
        }
        if n <= 0 {
            break;
        }
        out.clear();
        relay(&buf[..n as usize], &mut out);
        if write_all_fd(to, &out).is_err() {
            break;
        }
    }
    // Pass the end of this direction on to both ends. A client that hung
    // up (Ctrl+C in sudo, ssh killed) thereby reaches the Windows service
    // as EOF, which is what cancels a pending fingerprint gate.
    unsafe {
        libc::shutdown(to, libc::SHUT_WR);
        libc::shutdown(from, libc::SHUT_RD);
    }
}

fn write_all_fd(fd: RawFd, mut data: &[u8]) -> std::io::Result<()> {
    while !data.is_empty() {
        let n = unsafe { libc::write(fd, data.as_ptr().cast(), data.len()) };
        if n < 0 {
            let e = std::io::Error::last_os_error();
            if e.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            return Err(e);
        }
        data = &data[n as usize..];
    }
    Ok(())
}

// ── check ─────────────────────────────────────────────────────

/// One round trip to the Windows service. Exit code is the verdict the
/// Windows client reads after installing into a distro.
fn check() -> ExitCode {
    println!("bridge-version: {}", env!("CARGO_PKG_VERSION"));
    if let Some(path) = native_daemon_present() {
        println!("result: conflict with native immurok daemon ({path})");
        return ExitCode::from(EXIT_NATIVE_DAEMON);
    }
    let Some(token) = read_token() else {
        println!("result: {TOKEN_FILE} missing or malformed");
        return ExitCode::from(EXIT_NO_TOKEN);
    };
    let host = match vsock_connect(CLI_PORT) {
        Ok(fd) => fd,
        Err(e) => {
            println!("result: Windows service unreachable ({e})");
            return ExitCode::from(EXIT_UNAVAILABLE);
        }
    };
    let uid = unsafe { libc::geteuid() };
    let mut msg = preamble(&token, uid, std::process::id(), &distro_name());
    msg.push_str("STATUS\n");
    let mut file = fs::File::from(host);
    if file.write_all(msg.as_bytes()).is_err() {
        println!("result: Windows service unreachable (write failed)");
        return ExitCode::from(EXIT_UNAVAILABLE);
    }
    let mut reply = String::new();
    let _ = file.read_to_string(&mut reply);
    let reply = reply.trim();
    if reply.is_empty() {
        // The service drops the connection without a word on a bad token.
        println!("result: rejected by Windows service (token mismatch?)");
        return ExitCode::from(EXIT_FAILED);
    }
    println!("service: {reply}");
    if !Path::new(SYSTEM_RUNTIME_DIR).join(PAM_SOCKET_NAME).exists() {
        println!("result: bridge is not running ({SYSTEM_RUNTIME_DIR}/{PAM_SOCKET_NAME} missing)");
        return ExitCode::from(EXIT_FAILED);
    }
    println!("result: ok");
    ExitCode::from(EXIT_OK)
}

// ── helpers ───────────────────────────────────────────────────

fn read_token() -> Option<String> {
    let t = fs::read_to_string(TOKEN_FILE).ok()?;
    let t = t.trim();
    (t.len() == 64 && t.bytes().all(|b| b.is_ascii_hexdigit())).then(|| t.to_string())
}

/// systemd units do not inherit WSL_DISTRO_NAME, so the installer records
/// the name; the environment is the fallback for a hand-started bridge.
fn distro_name() -> String {
    let raw = fs::read_to_string(DISTRO_FILE)
        .ok()
        .or_else(|| std::env::var("WSL_DISTRO_NAME").ok())
        .unwrap_or_default();
    let clean = sanitize(raw.trim());
    if clean.is_empty() {
        "unknown".to_string()
    } else {
        clean
    }
}

/// Preamble values are space-separated `key=value`; keep them to a
/// conservative character set so a hostile comm/distro cannot inject keys.
fn sanitize(s: &str) -> String {
    s.chars()
        .take(64)
        .map(|c| if c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.') { c } else { '?' })
        .collect()
}

fn native_daemon_present() -> Option<&'static str> {
    [
        "/usr/lib/systemd/system/immurok-daemon.service",
        "/lib/systemd/system/immurok-daemon.service",
        "/etc/systemd/system/immurok-daemon.service",
        "/usr/bin/immurok-daemon",
        "/usr/local/bin/immurok-daemon",
    ]
    .into_iter()
    .find(|p| Path::new(p).exists())
}

fn preamble(token: &str, uid: u32, pid: u32, distro: &str) -> String {
    let chain = pid_chain(pid);
    let pids = chain.iter().map(u32::to_string).collect::<Vec<_>>().join(",");
    let comm = fs::read_to_string(format!("/proc/{pid}/comm"))
        .map(|s| sanitize(s.trim()))
        .unwrap_or_default();
    let age = process_age_secs(pid).map(|a| a.to_string()).unwrap_or_default();
    let uid = if uid == u32::MAX { String::new() } else { uid.to_string() };
    // hints=1: this bridge understands (and strips) the service's progress
    // frames on agent connections. A service must not send them otherwise —
    // forwarded to ssh they would be read as the answer.
    format!("IMK-WSL/1 token={token} uid={uid} distro={distro} pid={pid} pids={pids} comm={comm} age={age} hints=1\n")
}

/// `pid` and its ancestors, self first, up to MAX_PID_CHAIN entries.
fn pid_chain(pid: u32) -> Vec<u32> {
    let mut chain = Vec::new();
    let mut cur = pid;
    while cur > 0 && chain.len() < MAX_PID_CHAIN {
        chain.push(cur);
        match stat_fields(cur).and_then(|f| f.get(1)?.parse::<u32>().ok()) {
            Some(ppid) if ppid != cur => cur = ppid,
            _ => break,
        }
    }
    chain
}

/// Fields of /proc/<pid>/stat after the `(comm)` part: index 0 is state,
/// 1 is ppid, 19 is starttime (field 22 of the full line).
fn stat_fields(pid: u32) -> Option<Vec<String>> {
    let stat = fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    let rest = &stat[stat.rfind(')')? + 1..];
    Some(rest.split_whitespace().map(str::to_string).collect())
}

fn process_age_secs(pid: u32) -> Option<u64> {
    let start_ticks: u64 = stat_fields(pid)?.get(19)?.parse().ok()?;
    let hz = unsafe { libc::sysconf(libc::_SC_CLK_TCK) };
    if hz <= 0 {
        return None;
    }
    let uptime: f64 = fs::read_to_string("/proc/uptime").ok()?.split_whitespace().next()?.parse().ok()?;
    Some((uptime - start_ticks as f64 / hz as f64).max(0.0) as u64)
}

fn peer_cred(fd: RawFd) -> Option<(u32, u32)> {
    let mut cred: libc::ucred = unsafe { mem::zeroed() };
    let mut len = mem::size_of::<libc::ucred>() as libc::socklen_t;
    let rc = unsafe {
        libc::getsockopt(fd, libc::SOL_SOCKET, libc::SO_PEERCRED, (&mut cred as *mut libc::ucred).cast(), &mut len)
    };
    (rc == 0).then_some((cred.uid, cred.pid as u32))
}

/// Connect to the Windows host (CID 2) on a vsock port. Fails within the
/// kernel's vsock connect timeout (about 2 s) when nothing listens.
fn vsock_connect(port: u32) -> std::io::Result<OwnedFd> {
    let fd = unsafe { libc::socket(libc::AF_VSOCK, libc::SOCK_STREAM | libc::SOCK_CLOEXEC, 0) };
    if fd < 0 {
        return Err(std::io::Error::last_os_error());
    }
    let fd = unsafe { OwnedFd::from_raw_fd(fd) };
    let mut addr: libc::sockaddr_vm = unsafe { mem::zeroed() };
    addr.svm_family = libc::AF_VSOCK as libc::sa_family_t;
    addr.svm_cid = libc::VMADDR_CID_HOST;
    addr.svm_port = port;
    let rc = unsafe {
        libc::connect(
            fd.as_raw_fd(),
            (&addr as *const libc::sockaddr_vm).cast(),
            mem::size_of::<libc::sockaddr_vm>() as libc::socklen_t,
        )
    };
    if rc != 0 {
        return Err(std::io::Error::last_os_error());
    }
    Ok(fd)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn auth_only_from_root() {
        assert!(cli_request_allowed(b"AUTH:alice:sudo", 0));
        assert!(!cli_request_allowed(b"AUTH:alice:sudo", 1000));
        assert!(cli_request_allowed(b"AGENT_APPROVE:ls\n", 1000));
        assert!(cli_request_allowed(b"LIST:ssh\n", 1000));
        assert!(cli_request_allowed(b"STATUS\n", 1000));
    }

    #[test]
    fn sanitize_blocks_key_injection() {
        assert_eq!(sanitize("Ubuntu-24.04"), "Ubuntu-24.04");
        assert_eq!(sanitize("a b=c\nd"), "a?b?c?d");
        assert_eq!(sanitize(&"x".repeat(100)).len(), 64);
    }

    #[test]
    fn preamble_shape() {
        let p = preamble(&"a".repeat(64), 0, std::process::id(), "Ubuntu");
        assert!(p.starts_with("IMK-WSL/1 token="));
        assert!(p.ends_with('\n'));
        assert_eq!(p.matches('\n').count(), 1);
        assert!(p.contains(" uid=0 distro=Ubuntu pid="));
        // self first in the chain
        let pids = p.split(" pids=").nth(1).unwrap().split(' ').next().unwrap();
        assert_eq!(pids.split(',').next().unwrap(), std::process::id().to_string());
    }

    #[test]
    fn unknown_uid_is_blank() {
        assert!(preamble(&"a".repeat(64), u32::MAX, 1, "d").contains(" uid= distro=d "));
    }
}
