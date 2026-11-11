//! The fingerprint line on the terminal of an `ssh` / `git` that is waiting
//! for a signature.
//!
//! On native Linux the daemon knows what the fingerprint gate is doing and
//! tells the session agent to draw this line. Here the component that knows
//! is the Windows service, and it cannot reach a terminal inside WSL. The
//! bridge can: it is root, and SO_PEERCRED gives it the pid of the `ssh`
//! that connected. So the two halves meet on the agent connection itself:
//!
//!   * client → service: the bridge only watches. A SIGN_REQUEST going by
//!     starts the line.
//!   * service → client: the service interleaves *hint frames* (type 0xF1,
//!     see `ImmurokService/Wsl/WslHint.cs`) with the real responses — gate
//!     open, finger not matched and tries left, verified and signing, why
//!     it failed. The bridge draws them and **removes them from the
//!     stream**; everything else is forwarded byte for byte.
//!
//! The service sends hint frames only to a bridge that announced `hints=1`
//! in its preamble. Against a service that sends none, the line still
//! appears and still ends with the response, just without the states in
//! between.
//!
//! Only an enum code and a count cross the wire. The text is fixed and
//! lives here; nothing else from the stream — key comments, host names —
//! ever reaches the terminal: a pts interprets what it is given, and this
//! process is root.

use std::collections::VecDeque;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

const SSH_AGENT_FAILURE: u8 = 5;
const SSH_AGENTC_SIGN_REQUEST: u8 = 13;

/// Hint frame: `[u32 BE len = 3][0xF1][code][arg]`. Mirrors WslHint.cs.
const HINT_FRAME: u8 = 0xF1;
const HINT_WAITING: u8 = 1;
const HINT_MISMATCH: u8 = 2;
const HINT_SIGNING: u8 = 3;
const HINT_FAIL_REJECTED: u8 = 4;
const HINT_FAIL_TIMEOUT: u8 = 5;
const HINT_FAIL_OTHER: u8 = 6;

/// One state of the line. Wording and colours follow the native Linux hint
/// (app-linux-rs `tty_hint.rs`): yellow while waiting, red on a rejection,
/// green once the touch is in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Hint {
    Waiting,
    Mismatch { remaining: u8 },
    Signing,
    FailRejected,
    FailTimeout,
    FailOther,
}

impl Hint {
    fn from_wire(code: u8, arg: u8) -> Option<Hint> {
        Some(match code {
            HINT_WAITING => Hint::Waiting,
            HINT_MISMATCH => Hint::Mismatch { remaining: arg },
            HINT_SIGNING => Hint::Signing,
            HINT_FAIL_REJECTED => Hint::FailRejected,
            HINT_FAIL_TIMEOUT => Hint::FailTimeout,
            HINT_FAIL_OTHER => Hint::FailOther,
            _ => return None, // a newer service; ignore what we cannot name
        })
    }

    fn is_final(self) -> bool {
        matches!(self, Hint::FailRejected | Hint::FailTimeout | Hint::FailOther)
    }

    /// Every string is a literal; the attempt count is the only variable.
    fn message(self) -> String {
        match self {
            Hint::Waiting => "\x1b[33mPlease verify your fingerprint...\x1b[0m".into(),
            Hint::Mismatch { remaining } => {
                let noun = if remaining == 1 { "attempt" } else { "attempts" };
                format!("\x1b[31m✗ Fingerprint not recognized\x1b[0m — {remaining} {noun} left, touch again...")
            }
            Hint::Signing => "\x1b[32m✓ Verified — signing...\x1b[0m".into(),
            Hint::FailRejected => "\x1b[31m✗ Fingerprint not recognized — verification failed\x1b[0m".into(),
            Hint::FailTimeout => "\x1b[31m✗ Fingerprint verification timed out\x1b[0m".into(),
            Hint::FailOther => "\x1b[31m✗ Fingerprint verification failed\x1b[0m".into(),
        }
    }
}

/// A signature that needs no touch (the device's cooldown is still open)
/// comes back within a few tens of milliseconds; drawing and erasing a line
/// for it would only flicker. A hint frame cancels the wait: then we know
/// there is something to say.
const DRAW_DELAY: Duration = Duration::from_millis(150);
const FRAME_INTERVAL: Duration = Duration::from_millis(80);
/// Far above any real agent message; a length beyond it means we lost
/// framing and must stop interpreting.
const MAX_FRAME: usize = 1024 * 1024;

/// What a [`FrameReader`] found at the start of a frame.
#[derive(Debug, PartialEq, Eq)]
enum Event {
    /// An ordinary agent message of this type (forwarded).
    Message(u8),
    /// A hint frame (consumed, not forwarded). `None` if malformed.
    Hint(Option<(u8, u8)>),
}

/// Follows the `[u32 BE length][type][body]` framing of one direction of an
/// ssh-agent stream. Frames may be split across reads anywhere, including
/// inside the header.
///
/// With `strip_hints` it is also a filter: hint frames are dropped from the
/// output. That is the only thing it ever removes — once framing is lost
/// it turns into a plain copy for the rest of the connection.
struct FrameReader {
    strip_hints: bool,
    header: [u8; 5],
    header_len: usize,
    body_left: usize,
    /// Body of the hint frame being consumed (never more than a few bytes kept).
    hint_body: Option<Vec<u8>>,
    hint_well_formed: bool,
    lost: bool,
}

impl FrameReader {
    fn new(strip_hints: bool) -> FrameReader {
        FrameReader { strip_hints, header: [0; 5], header_len: 0, body_left: 0, hint_body: None, hint_well_formed: false, lost: false }
    }

    /// Consume `chunk`; append what must be forwarded to `out`; return what
    /// started in it.
    fn feed(&mut self, mut chunk: &[u8], out: &mut Vec<u8>) -> Vec<Event> {
        let mut events = Vec::new();
        while !chunk.is_empty() {
            if self.lost {
                out.extend_from_slice(chunk);
                break;
            }
            if self.body_left > 0 {
                let n = self.body_left.min(chunk.len());
                match &mut self.hint_body {
                    Some(body) => {
                        let room = 2usize.saturating_sub(body.len());
                        body.extend_from_slice(&chunk[..n.min(room)]);
                    }
                    None => out.extend_from_slice(&chunk[..n]),
                }
                self.body_left -= n;
                chunk = &chunk[n..];
                if self.body_left == 0 {
                    if let Some(body) = self.hint_body.take() {
                        let well_formed = self.hint_well_formed && body.len() == 2;
                        events.push(Event::Hint(well_formed.then(|| (body[0], body[1]))));
                    }
                }
                continue;
            }
            // Header bytes are held back until all five are in: only then
            // do we know whether this frame is forwarded at all.
            let take = (5 - self.header_len).min(chunk.len());
            self.header[self.header_len..self.header_len + take].copy_from_slice(&chunk[..take]);
            self.header_len += take;
            chunk = &chunk[take..];
            if self.header_len < 5 {
                break;
            }
            self.header_len = 0;
            let len = u32::from_be_bytes([self.header[0], self.header[1], self.header[2], self.header[3]]) as usize;
            if len == 0 || len > MAX_FRAME {
                self.lost = true;
                out.extend_from_slice(&self.header);
                continue;
            }
            let kind = self.header[4];
            self.body_left = len - 1;
            if self.strip_hints && kind == HINT_FRAME {
                // Exactly [code][arg]. Any other length is dropped whole.
                self.hint_well_formed = self.body_left == 2;
                self.hint_body = Some(Vec::with_capacity(2));
                if self.body_left == 0 {
                    self.hint_body = None;
                    events.push(Event::Hint(None));
                }
            } else {
                out.extend_from_slice(&self.header);
                events.push(Event::Message(kind));
            }
        }
        events
    }
}

/// The hint for one agent connection. `client_bytes` / `host_bytes` are
/// called from the two pump threads with every chunk they read; each
/// appends to `out` what the pump must write on.
pub struct SignHint {
    pid: u32,
    state: Mutex<State>,
}

struct State {
    from_client: FrameReader,
    from_host: FrameReader,
    /// Request types awaiting their response, oldest first. The agent
    /// protocol answers strictly in order, so the front is what the next
    /// response belongs to.
    outstanding: VecDeque<u8>,
    spinner: Option<Spinner>,
    /// The reason the service gave for a failure, to leave on the terminal
    /// when the FAILURE response arrives.
    failure: Option<Hint>,
}

impl SignHint {
    pub fn new(pid: u32) -> Arc<SignHint> {
        Arc::new(SignHint {
            pid,
            state: Mutex::new(State {
                from_client: FrameReader::new(false),
                from_host: FrameReader::new(true),
                outstanding: VecDeque::new(),
                spinner: None,
                failure: None,
            }),
        })
    }

    pub fn client_bytes(&self, chunk: &[u8], out: &mut Vec<u8>) {
        let Ok(mut st) = self.state.lock() else {
            out.extend_from_slice(chunk);
            return;
        };
        let st = &mut *st;
        // Never a filter in this direction: forward first, exactly as read.
        out.extend_from_slice(chunk);
        let mut scratch = Vec::new();
        for ev in st.from_client.feed(chunk, &mut scratch) {
            let Event::Message(t) = ev else { continue };
            st.outstanding.push_back(t);
            if t == SSH_AGENTC_SIGN_REQUEST && st.spinner.is_none() {
                st.failure = None;
                st.spinner = client_tty(self.pid).and_then(|tty| Spinner::start(tty, Hint::Waiting.message()));
            }
        }
    }

    pub fn host_bytes(&self, chunk: &[u8], out: &mut Vec<u8>) {
        let Ok(mut st) = self.state.lock() else {
            out.extend_from_slice(chunk);
            return;
        };
        let st = &mut *st;
        for ev in st.from_host.feed(chunk, out) {
            match ev {
                Event::Hint(Some((code, arg))) => {
                    // A hint only means something while a signature is pending.
                    if st.outstanding.front() != Some(&SSH_AGENTC_SIGN_REQUEST) {
                        continue;
                    }
                    let Some(hint) = Hint::from_wire(code, arg) else { continue };
                    if hint.is_final() {
                        st.failure = Some(hint);
                    } else if let Some(sp) = &st.spinner {
                        sp.show_now(hint.message());
                    }
                }
                Event::Hint(None) => {}
                Event::Message(t) => {
                    if st.outstanding.pop_front() != Some(SSH_AGENTC_SIGN_REQUEST) {
                        continue;
                    }
                    let failure = st.failure.take();
                    if let Some(sp) = st.spinner.take() {
                        if t == SSH_AGENT_FAILURE {
                            sp.finish(Some(failure.unwrap_or(Hint::FailOther).message()));
                        } else {
                            sp.finish(None);
                        }
                    }
                }
            }
        }
    }

    /// The client went away mid-wait (Ctrl+C). Its shell has already taken
    /// the terminal back and printed a prompt on this line, so stop drawing
    /// and write nothing — erasing now would wipe that prompt.
    pub fn client_gone(&self) {
        if let Ok(mut st) = self.state.lock() {
            if let Some(sp) = st.spinner.take() {
                sp.abandon();
            }
        }
    }

    /// Connection over. Anything still on screen is stale.
    pub fn closed(&self) {
        if let Ok(mut st) = self.state.lock() {
            if let Some(sp) = st.spinner.take() {
                sp.finish(None);
            }
        }
    }
}

/// The terminal `pid` is attached to. ssh keeps stderr on the terminal even
/// when git pipes its stdin/stdout, so fd 2 first. `None` when no fd is a
/// terminal (cron, CI, an agent with everything piped): then there is nobody
/// to show a hint to.
fn client_tty(pid: u32) -> Option<PathBuf> {
    if pid == 0 {
        return None;
    }
    [2, 0, 1]
        .into_iter()
        .filter_map(|fd| fs::read_link(format!("/proc/{pid}/fd/{fd}")).ok())
        .find(|p| is_terminal_path(p))
}

fn is_terminal_path(p: &Path) -> bool {
    let Some(s) = p.to_str() else { return false };
    // "/dev/pts/" with the slash, so "/dev/ptsfoo" does not match.
    s.starts_with("/dev/pts/") || s.starts_with("/dev/tty")
}

/// One animated line on one terminal, redrawn in place by its own thread.
struct Spinner {
    stop: Arc<AtomicBool>,
    /// Skip what is left of the initial delay.
    hurry: Arc<AtomicBool>,
    drawn: Arc<AtomicBool>,
    msg: Arc<Mutex<String>>,
    join: Option<JoinHandle<()>>,
    tty: PathBuf,
}

impl Spinner {
    fn start(tty: PathBuf, msg: String) -> Option<Spinner> {
        let mut f = OpenOptions::new().write(true).open(&tty).ok()?;
        let stop = Arc::new(AtomicBool::new(false));
        let hurry = Arc::new(AtomicBool::new(false));
        let drawn = Arc::new(AtomicBool::new(false));
        let msg = Arc::new(Mutex::new(msg));
        let (stop_t, hurry_t, drawn_t, msg_t) = (stop.clone(), hurry.clone(), drawn.clone(), msg.clone());
        let join = thread::Builder::new()
            .name("imk-tty-hint".into())
            .spawn(move || {
                const FRAMES: [&str; 10] = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
                if sleep_unless(&stop_t, Some(&hurry_t), DRAW_DELAY) {
                    return;
                }
                let mut i = 0usize;
                while !stop_t.load(Ordering::Relaxed) {
                    drawn_t.store(true, Ordering::Relaxed);
                    let line = msg_t.lock().map(|g| g.clone()).unwrap_or_default();
                    // \r + erase-line + yellow frame + " " + message (self-coloured)
                    let _ = write!(f, "\r\x1b[K\x1b[33m{}\x1b[0m {}", FRAMES[i % FRAMES.len()], line);
                    let _ = f.flush();
                    i += 1;
                    if sleep_unless(&stop_t, None, FRAME_INTERVAL) {
                        return;
                    }
                }
            })
            .ok()?;
        Some(Spinner { stop, hurry, drawn, msg, join: Some(join), tty })
    }

    /// Change the text and make sure the line is on screen.
    fn show_now(&self, m: String) {
        if let Ok(mut g) = self.msg.lock() {
            *g = m;
        }
        self.hurry.store(true, Ordering::Relaxed);
    }

    fn halt(&mut self) -> bool {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(j) = self.join.take() {
            let _ = j.join();
        }
        self.drawn.load(Ordering::Relaxed)
    }

    /// `Some(line)` leaves that line on the terminal; `None` erases ours.
    /// A spinner that never got to draw leaves the terminal untouched unless
    /// there is a line to leave.
    fn finish(mut self, final_line: Option<String>) {
        let drawn = self.halt();
        if !drawn && final_line.is_none() {
            return;
        }
        if let Ok(mut f) = OpenOptions::new().write(true).open(&self.tty) {
            let _ = match final_line {
                Some(l) => write!(f, "\r\x1b[K{l}\n"),
                None => f.write_all(b"\r\x1b[K"),
            };
            let _ = f.flush();
        }
    }

    fn abandon(mut self) {
        self.halt();
    }
}

impl Drop for Spinner {
    fn drop(&mut self) {
        // Safety net only: every path above consumes the spinner first.
        self.halt();
    }
}

/// Sleep in short slices. `true` if `stop` was raised meanwhile; returns
/// early with `false` if `hurry` was.
fn sleep_unless(stop: &AtomicBool, hurry: Option<&AtomicBool>, total: Duration) -> bool {
    let slice = Duration::from_millis(10);
    let mut left = total;
    while !left.is_zero() {
        if stop.load(Ordering::Relaxed) {
            return true;
        }
        if hurry.is_some_and(|h| h.load(Ordering::Relaxed)) {
            return false;
        }
        let d = left.min(slice);
        thread::sleep(d);
        left -= d;
    }
    stop.load(Ordering::Relaxed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;
    use std::os::fd::FromRawFd;

    fn frame(t: u8, body: &[u8]) -> Vec<u8> {
        let mut v = ((body.len() + 1) as u32).to_be_bytes().to_vec();
        v.push(t);
        v.extend_from_slice(body);
        v
    }

    fn hint_frame(code: u8, arg: u8) -> Vec<u8> {
        frame(HINT_FRAME, &[code, arg])
    }

    #[test]
    fn reader_reports_each_frame_once_and_copies_everything() {
        let mut bytes = frame(11, &[]);
        bytes.extend(frame(13, &[0; 300]));
        bytes.extend(frame(27, b"x"));
        let mut r = FrameReader::new(false);
        let mut out = Vec::new();
        let ev = r.feed(&bytes, &mut out);
        assert_eq!(ev, vec![Event::Message(11), Event::Message(13), Event::Message(27)]);
        assert_eq!(out, bytes);
        // not stripping: a 0xF1 frame is just another message
        let mut out = Vec::new();
        assert_eq!(FrameReader::new(false).feed(&hint_frame(1, 0), &mut out), vec![Event::Message(HINT_FRAME)]);
        assert_eq!(out, hint_frame(1, 0));
    }

    /// The property the filter exists for: whatever the split, the output is
    /// the input minus exactly the hint frames, and each hint is seen once.
    #[test]
    fn filter_removes_exactly_the_hint_frames_at_any_split() {
        let mut input = Vec::new();
        let mut expected = Vec::new();
        for part in [frame(12, &[7; 20]), hint_frame(HINT_WAITING, 0), hint_frame(HINT_MISMATCH, 2)] {
            input.extend(&part);
            if part[4] != HINT_FRAME {
                expected.extend(&part);
            }
        }
        for part in [frame(14, &[9; 70]), hint_frame(HINT_SIGNING, 0), frame(5, &[])] {
            input.extend(&part);
            if part[4] != HINT_FRAME {
                expected.extend(&part);
            }
        }
        let want_events = vec![
            Event::Message(12),
            Event::Hint(Some((HINT_WAITING, 0))),
            Event::Hint(Some((HINT_MISMATCH, 2))),
            Event::Message(14),
            Event::Hint(Some((HINT_SIGNING, 0))),
            Event::Message(5),
        ];
        for cut in 0..=input.len() {
            let mut r = FrameReader::new(true);
            let mut out = Vec::new();
            let mut ev = r.feed(&input[..cut], &mut out);
            ev.extend(r.feed(&input[cut..], &mut out));
            assert_eq!(out, expected, "bytes, split at {cut}");
            assert_eq!(ev, want_events, "events, split at {cut}");
        }
        // byte at a time
        let mut r = FrameReader::new(true);
        let mut out = Vec::new();
        let ev: Vec<Event> = input.iter().flat_map(|b| r.feed(&[*b], &mut out)).collect();
        assert_eq!(out, expected);
        assert_eq!(ev, want_events);
    }

    #[test]
    fn odd_hint_frames_are_dropped_without_derailing_the_stream() {
        for odd in [frame(HINT_FRAME, &[]), frame(HINT_FRAME, &[1]), frame(HINT_FRAME, &[1, 2, 3, 4, 5])] {
            let mut input = odd.clone();
            input.extend(frame(14, &[1, 2, 3]));
            let mut r = FrameReader::new(true);
            let mut out = Vec::new();
            let ev = r.feed(&input, &mut out);
            assert_eq!(out, frame(14, &[1, 2, 3]), "{odd:?}");
            assert_eq!(ev, vec![Event::Hint(None), Event::Message(14)], "{odd:?}");
        }
    }

    #[test]
    fn lost_framing_turns_the_filter_into_a_plain_copy() {
        // Nothing may be withheld or dropped once we stop understanding the stream.
        let mut input = vec![0xff, 0xff, 0xff, 0xff, 13, 1, 2, 3];
        input.extend(hint_frame(1, 0));
        input.extend(frame(14, &[4]));
        for cut in 0..=input.len() {
            let mut r = FrameReader::new(true);
            let mut out = Vec::new();
            let mut ev = r.feed(&input[..cut], &mut out);
            ev.extend(r.feed(&input[cut..], &mut out));
            assert_eq!(out, input, "split at {cut}");
            assert!(ev.is_empty(), "split at {cut}");
        }
        let mut out = Vec::new();
        FrameReader::new(true).feed(&[0, 0, 0, 0, 13, 9], &mut out); // zero length
        assert_eq!(out, vec![0, 0, 0, 0, 13, 9]);
    }

    #[test]
    fn only_terminal_devices_are_written_to() {
        for good in ["/dev/pts/0", "/dev/pts/17", "/dev/tty", "/dev/tty3"] {
            assert!(is_terminal_path(Path::new(good)), "{good}");
        }
        for bad in ["/dev/null", "/dev/ptsfoo", "/dev/ptmx", "/tmp/pts/0", "pipe:[12345]", "socket:[1]"] {
            assert!(!is_terminal_path(Path::new(bad)), "{bad}");
        }
    }

    #[test]
    fn every_message_carries_nothing_but_colour() {
        // Guards the injection surface: SGR sequences only, no OSC, no
        // cursor movement, no newline — for every hint, every count.
        let mut all = vec![Hint::Waiting, Hint::Signing, Hint::FailRejected, Hint::FailTimeout, Hint::FailOther];
        all.extend((0..=255u8).map(|remaining| Hint::Mismatch { remaining }));
        for h in all {
            let m = h.message();
            assert!(!m.contains('\n') && !m.contains('\r') && !m.contains("\x1b]"), "{h:?}");
            let mut rest = m.as_str();
            while let Some(i) = rest.find('\x1b') {
                let tail = &rest[i + 1..];
                assert!(tail.starts_with('['), "non-CSI escape in {h:?}");
                let end = tail.find(|c: char| c.is_ascii_alphabetic()).unwrap();
                assert_eq!(&tail[end..=end], "m", "non-SGR sequence in {h:?}");
                rest = &tail[end + 1..];
            }
        }
        assert!(Hint::Mismatch { remaining: 1 }.message().contains("1 attempt left"));
        assert!(Hint::Mismatch { remaining: 2 }.message().contains("2 attempts left"));
    }

    #[test]
    fn unknown_hint_codes_are_ignored() {
        assert_eq!(Hint::from_wire(0, 0), None);
        assert_eq!(Hint::from_wire(99, 3), None);
        assert_eq!(Hint::from_wire(HINT_MISMATCH, 2), Some(Hint::Mismatch { remaining: 2 }));
    }

    /// A real pty: the slave path is what the spinner opens in production.
    fn open_pty() -> (fs::File, PathBuf) {
        let (mut master, mut slave) = (0, 0);
        let rc = unsafe {
            libc::openpty(&mut master, &mut slave, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut())
        };
        assert_eq!(rc, 0, "openpty failed");
        let name = unsafe { std::ffi::CStr::from_ptr(libc::ptsname(master)) }.to_string_lossy().to_string();
        unsafe {
            libc::close(slave);
            libc::fcntl(master, libc::F_SETFL, libc::O_NONBLOCK);
        }
        (unsafe { fs::File::from_raw_fd(master) }, PathBuf::from(name))
    }

    fn read_for(master: &mut fs::File, ms: u64) -> String {
        let mut seen = String::new();
        for _ in 0..ms / 10 {
            let mut buf = [0u8; 4096];
            if let Ok(n) = master.read(&mut buf) {
                seen.push_str(&String::from_utf8_lossy(&buf[..n]));
            }
            thread::sleep(Duration::from_millis(10));
        }
        seen
    }

    #[test]
    fn spinner_draws_then_erases() {
        let (mut master, path) = open_pty();
        let sp = Spinner::start(path, Hint::Waiting.message()).unwrap();
        let seen = read_for(&mut master, 400);
        assert!(seen.contains("Please verify your fingerprint"), "{seen:?}");
        assert!(seen.contains("\r\x1b[K"), "{seen:?}");
        sp.finish(None);
        let tail = read_for(&mut master, 100);
        assert!(tail.ends_with("\r\x1b[K"), "{tail:?}");
        assert!(!tail.contains('\n'), "{tail:?}");
    }

    #[test]
    fn fast_answer_leaves_the_terminal_untouched() {
        let (mut master, path) = open_pty();
        let sp = Spinner::start(path, Hint::Waiting.message()).unwrap();
        thread::sleep(Duration::from_millis(30)); // well inside DRAW_DELAY
        sp.finish(None);
        assert_eq!(read_for(&mut master, 250), "");
    }

    #[test]
    fn failure_leaves_a_line_and_abandon_writes_nothing_more() {
        let (mut master, path) = open_pty();
        let sp = Spinner::start(path.clone(), Hint::Waiting.message()).unwrap();
        read_for(&mut master, 300);
        sp.finish(Some(Hint::FailTimeout.message()));
        let tail = read_for(&mut master, 100);
        assert!(tail.contains("timed out") && tail.ends_with('\n'), "{tail:?}");

        let sp = Spinner::start(path, Hint::Waiting.message()).unwrap();
        read_for(&mut master, 300);
        sp.abandon();
        read_for(&mut master, 50); // drain what was in flight
        assert_eq!(read_for(&mut master, 250), "", "abandon must not write");
    }

    /// Stand-in for the ssh client: a child process whose stderr is the pty
    /// slave, so `/proc/<pid>/fd/2` resolves to it like the real thing.
    fn child_on(tty: &Path) -> std::process::Child {
        let slave = OpenOptions::new().read(true).write(true).open(tty).unwrap();
        std::process::Command::new("sleep")
            .arg("30")
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(slave)
            .spawn()
            .unwrap()
    }

    #[test]
    fn a_mismatch_then_success_shows_every_state_and_ends_clean() {
        let (mut master, path) = open_pty();
        let mut child = child_on(&path);
        let h = SignHint::new(child.id());
        let mut fwd = Vec::new();

        h.client_bytes(&frame(13, &[1, 2, 3]), &mut fwd);
        assert_eq!(fwd, frame(13, &[1, 2, 3]), "requests are forwarded untouched");

        let mut to_client = Vec::new();
        h.host_bytes(&hint_frame(HINT_WAITING, 0), &mut to_client);
        let seen = read_for(&mut master, 200);
        assert!(seen.contains("Please verify your fingerprint"), "{seen:?}");

        h.host_bytes(&hint_frame(HINT_MISMATCH, 2), &mut to_client);
        let seen = read_for(&mut master, 200);
        assert!(seen.contains("not recognized") && seen.contains("2 attempts left"), "{seen:?}");

        h.host_bytes(&hint_frame(HINT_SIGNING, 0), &mut to_client);
        let seen = read_for(&mut master, 200);
        assert!(seen.contains("Verified — signing"), "{seen:?}");
        assert!(to_client.is_empty(), "hint frames must not reach the client");

        h.host_bytes(&frame(14, &[0; 70]), &mut to_client);
        assert_eq!(to_client, frame(14, &[0; 70]));
        let tail = read_for(&mut master, 100);
        assert!(tail.ends_with("\r\x1b[K") && !tail.contains('\n'), "line not erased: {tail:?}");

        let _ = child.kill();
        let _ = child.wait();
    }

    #[test]
    fn a_failure_leaves_the_reason_the_service_gave() {
        for (code, want) in [
            (HINT_FAIL_REJECTED, "not recognized — verification failed"),
            (HINT_FAIL_TIMEOUT, "timed out"),
            (HINT_FAIL_OTHER, "verification failed"),
        ] {
            let (mut master, path) = open_pty();
            let mut child = child_on(&path);
            let h = SignHint::new(child.id());
            let mut sink = Vec::new();
            h.client_bytes(&frame(13, &[]), &mut sink);
            let mut to_client = Vec::new();
            h.host_bytes(&hint_frame(HINT_WAITING, 0), &mut to_client);
            read_for(&mut master, 150);
            // reason and FAILURE arriving in one read, as they do on the wire
            let mut both = hint_frame(code, 0);
            both.extend(frame(SSH_AGENT_FAILURE, &[]));
            h.host_bytes(&both, &mut to_client);
            assert_eq!(to_client, frame(SSH_AGENT_FAILURE, &[]));
            let tail = read_for(&mut master, 100);
            assert!(tail.contains(want) && tail.ends_with('\n'), "{code}: {tail:?}");
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    #[test]
    fn without_hint_frames_the_line_still_ends_with_the_response() {
        // An older service: no hint frames at all.
        let (mut master, path) = open_pty();
        let mut child = child_on(&path);
        let h = SignHint::new(child.id());
        let mut sink = Vec::new();
        h.client_bytes(&frame(13, &[]), &mut sink);
        let seen = read_for(&mut master, 400);
        assert!(seen.contains("Please verify your fingerprint"), "{seen:?}");
        h.host_bytes(&frame(SSH_AGENT_FAILURE, &[]), &mut sink);
        let tail = read_for(&mut master, 100);
        assert!(tail.contains("verification failed") && tail.ends_with('\n'), "{tail:?}");
        let _ = child.kill();
        let _ = child.wait();
    }

    #[test]
    fn hints_outside_a_pending_signature_are_ignored() {
        let (mut master, path) = open_pty();
        let mut child = child_on(&path);
        let h = SignHint::new(child.id());
        let mut sink = Vec::new();
        h.client_bytes(&frame(11, &[]), &mut sink); // list identities
        let mut to_client = Vec::new();
        h.host_bytes(&hint_frame(HINT_SIGNING, 0), &mut to_client);
        h.host_bytes(&frame(12, &[0; 4]), &mut to_client);
        assert_eq!(to_client, frame(12, &[0; 4]));
        assert_eq!(read_for(&mut master, 250), "", "nothing may be drawn");
        let _ = child.kill();
        let _ = child.wait();
    }

    #[test]
    fn requests_and_responses_pair_in_order() {
        // pid 0: no terminal, so no spinner — this exercises the pairing.
        let h = SignHint::new(0);
        let mut sink = Vec::new();
        h.client_bytes(&frame(11, &[]), &mut sink);
        h.client_bytes(&frame(13, &[1, 2, 3]), &mut sink);
        assert_eq!(h.state.lock().unwrap().outstanding, VecDeque::from([11, 13]));
        h.host_bytes(&frame(12, &[0; 8]), &mut sink);
        assert_eq!(h.state.lock().unwrap().outstanding, VecDeque::from([13]));
        h.host_bytes(&frame(14, &[0; 70]), &mut sink);
        assert!(h.state.lock().unwrap().outstanding.is_empty());
        h.client_gone();
        h.closed();
    }
}
