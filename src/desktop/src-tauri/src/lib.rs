use std::collections::VecDeque;
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::Deserialize;
use tauri::{AppHandle, Emitter, Manager};
use tauri_plugin_deep_link::DeepLinkExt;
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tauri_plugin_shell::ShellExt;
use tauri_plugin_updater::UpdaterExt;
use url::Url;

/// How long we wait for the sidecar's `studio.ready` line before the splash stops
/// spinning and starts explaining itself. The sidecar is *not* killed at this point —
/// a slow first scan of a big workspace still finishes and navigates — the deadline
/// only decides when the user gets told something. Override with
/// `TAP_STUDIO_STARTUP_TIMEOUT_SECS`; `0` waits forever (the old behaviour).
const DEFAULT_STARTUP_TIMEOUT: Duration = Duration::from_secs(45);

/// Once startup passes this mark the splash switches from a bare spinner to
/// "here is the phase it is stuck in", so a slow launch is diagnosable without
/// waiting for the full timeout.
const SLOW_START_AFTER: Duration = Duration::from_secs(8);

/// Ceiling on the login-shell PATH probe (see [`resolve_user_path`]). A shell rc
/// that blocks — waiting on a prompt, a slow network mount, a version manager —
/// would otherwise hang the whole launch before the sidecar is even spawned.
const PATH_PROBE_TIMEOUT: Duration = Duration::from_secs(5);

/// Sidecar output lines kept in memory for the failure screen. The full stream
/// always goes to the session log file.
const LOG_TAIL_LINES: usize = 60;

/// How long the SPA gets to paint something after the shell hands it the window.
/// Past this the page reports itself blank and the shell takes the window back —
/// see [`BLANK_WATCHDOG_JS`]. Override with `TAP_STUDIO_BLANK_TIMEOUT_SECS`; `0`
/// disables the watchdog.
const DEFAULT_BLANK_TIMEOUT: Duration = Duration::from_secs(12);

/// Ceiling on the pre-navigation probe (see [`probe_document`]) — connect and read
/// budget for a single loopback GET.
const PROBE_TIMEOUT: Duration = Duration::from_secs(5);

/// How long `navigate` keeps re-probing before it calls the launch broken. The
/// sidecar announces `ready` from `ApplicationStarted`, so the first GET should
/// succeed; the retries only cover a cold Windows install where Defender is still
/// reading the freshly self-extracted binary when the first request lands.
const PROBE_GRACE: Duration = Duration::from_secs(6);

/// Bytes of the probed document we are willing to read. We only need the status
/// line, the content type, and enough body to tell a real page from an error stub.
const PROBE_MAX_BYTES: usize = 16 * 1024;

/// Fragment key the blank-page watchdog uses to hand the window back to the splash
/// with an explanation. The SPA origin can already navigate itself anywhere, so
/// carrying the diagnosis in the URL costs no new privilege — unlike a `#[command]`,
/// which would have to be granted to `remote.urls` and would then be reachable by
/// anything that injects script into the workbench.
const FAIL_FRAGMENT_KEY: &str = "tapfail";

/// Ceiling on the SPA-supplied failure text. It arrives from the page's own error
/// handlers, so it is attacker-influenceable in the same way a response body is:
/// it goes on screen as `textContent` and is truncated here so a crafted error
/// cannot flood the log or the failure card.
const MAX_REPORTED_ERROR: usize = 600;

/// Ceiling on the session log. Everything the sidecar prints goes in it, and
/// ASP.NET's request logging is a few hundred bytes per request, so a long session
/// would grow the file without bound. Startup — the reason the file exists — is
/// always in the first few KB.
const MAX_LOG_BYTES: u64 = 4 * 1024 * 1024;

#[derive(serde::Serialize, Clone)]
struct UpdateInfo {
    version: String,
    current_version: String,
    body: Option<String>,
}

/// Check GitHub releases for a newer version. Returns `null` when the app is
/// already up to date, or an `UpdateInfo` object when an update is available.
/// Invoked from the webview (the sidecar's http origin) — reachable because
/// `capabilities/default.json` grants `allow-check-for-update` to that remote
/// origin and `build.rs` registers the command with the ACL.
#[tauri::command]
async fn check_for_update(app: tauri::AppHandle) -> Result<Option<UpdateInfo>, String> {
    let updater = app.updater().map_err(|e| e.to_string())?;
    match updater.check().await {
        Ok(Some(update)) => Ok(Some(UpdateInfo {
            version: update.version.clone(),
            current_version: update.current_version.clone(),
            body: update.body.clone(),
        })),
        Ok(None) => Ok(None),
        Err(e) => Err(e.to_string()),
    }
}

/// Download and install an available update, then restart the app. Silently
/// returns `Ok(())` if there is nothing to install.
#[tauri::command]
async fn install_update(app: tauri::AppHandle) -> Result<(), String> {
    let updater = app.updater().map_err(|e| e.to_string())?;
    let update = updater.check().await.map_err(|e| e.to_string())?;
    if let Some(update) = update {
        update
            .download_and_install(|_chunk_len, _content_len| {}, || {})
            .await
            .map_err(|e| e.to_string())?;
        app.restart();
    }
    Ok(())
}

/// Holds the URL the sidecar bound to. Stashed on the Tauri state so deep-link
/// handlers can build absolute callback URLs even before the webview has
/// finished navigating.
#[derive(Default)]
struct StudioApi(Mutex<Option<String>>);

/// One line of the sidecar's stdout handshake. `studio.ready` is the one that
/// matters — the others exist so a launch that never reaches ready can say *how
/// far* it got. Unknown/foreign lines simply fail to parse and are logged as-is.
#[derive(Deserialize)]
#[serde(tag = "event")]
enum SidecarEvent {
    #[serde(rename = "studio.ready")]
    Ready { url: String },
    /// A startup milestone: `phase` is a stable id (`workspace.loading`, …),
    /// `message` is human text good enough to put straight on the splash.
    #[serde(rename = "studio.progress")]
    Progress { phase: String, message: String },
    /// Startup went wrong in a way the sidecar could describe itself.
    #[serde(rename = "studio.error")]
    Error { phase: String, message: String },
}

/// What the splash renders. Pushed into the webview on every change; also the
/// shape the failure screen is built from.
#[derive(Clone, serde::Serialize)]
struct Status {
    /// `starting` | `failed`. `ready` never renders — the webview has navigated away.
    state: &'static str,
    /// Short label for the current step, e.g. "Waiting for the Studio service".
    phase: String,
    /// The detail line under it — the sidecar's own progress message once it has one.
    detail: String,
    /// What the user can do about a failure. Empty while things are fine.
    hint: String,
    elapsed_ms: u64,
    /// True once startup passed [`SLOW_START_AFTER`]; the splash reveals the detail line.
    slow: bool,
    log_path: String,
    log_tail: Vec<String>,
}

impl Status {
    fn starting() -> Self {
        Self {
            state: "starting",
            phase: "Starting Tap Studio".into(),
            detail: String::new(),
            hint: String::new(),
            elapsed_ms: 0,
            slow: false,
            log_path: String::new(),
            log_tail: Vec::new(),
        }
    }
}

/// Owns everything about "is the backend up yet": the session log, the current
/// phase, the spawned child, and the attempt counter that lets a retry ignore
/// the previous attempt's still-draining output.
struct Startup {
    /// Process start — the timeline the log file is stamped against.
    launched: Instant,
    /// Start of the *current* attempt; what the splash counts up from.
    attempt_started: Mutex<Instant>,
    attempt: AtomicU64,
    /// Set once the webview has been navigated at the sidecar. Stops the watchdog.
    navigated: AtomicBool,
    log_path: PathBuf,
    log: Mutex<Option<File>>,
    log_bytes: AtomicU64,
    tail: Mutex<VecDeque<String>>,
    status: Mutex<Status>,
    child: Mutex<Option<CommandChild>>,
    /// The bundle-served splash URL, captured from its own first page load. The
    /// scheme differs per platform (`tauri://localhost` vs `http://tauri.localhost`),
    /// so it is observed rather than constructed — and it is the address the shell
    /// navigates back to when the workbench turns out to be unrenderable.
    splash_url: Mutex<Option<String>>,
}

impl Startup {
    fn new(log_path: PathBuf, log: Option<File>) -> Self {
        Self {
            launched: Instant::now(),
            attempt_started: Mutex::new(Instant::now()),
            attempt: AtomicU64::new(0),
            navigated: AtomicBool::new(false),
            log_path,
            log: Mutex::new(log),
            log_bytes: AtomicU64::new(0),
            tail: Mutex::new(VecDeque::with_capacity(LOG_TAIL_LINES)),
            status: Mutex::new(Status::starting()),
            child: Mutex::new(None),
            splash_url: Mutex::new(None),
        }
    }

    fn attempt(&self) -> u64 {
        self.attempt.load(Ordering::SeqCst)
    }

    fn elapsed(&self) -> Duration {
        self.attempt_started.lock().unwrap().elapsed()
    }

    /// Append one line to the session log, the in-memory tail, and stderr (which
    /// is where `tauri dev` and `Console.app` pick it up).
    fn log(&self, line: &str) {
        let stamped = format!("[+{:>6}ms] {line}", self.launched.elapsed().as_millis());
        eprintln!("{stamped}");
        {
            let mut slot = self.log.lock().unwrap();
            if let Some(file) = slot.as_mut() {
                let _ = writeln!(file, "{stamped}");
                let _ = file.flush();
                let written = self
                    .log_bytes
                    .fetch_add(stamped.len() as u64 + 1, Ordering::Relaxed);
                if written > MAX_LOG_BYTES {
                    let _ = writeln!(
                        file,
                        "[log closed at {MAX_LOG_BYTES} bytes — relaunch Tap Studio for a fresh one]"
                    );
                    let _ = file.flush();
                    *slot = None;
                }
            }
        }
        let mut tail = self.tail.lock().unwrap();
        if tail.len() == LOG_TAIL_LINES {
            tail.pop_front();
        }
        tail.push_back(stamped);
    }

    /// Record a new step. A step that arrives *after* a failure only refreshes the
    /// detail line — the sidecar is still alive and still reporting, but the user
    /// has already been told the launch is not going well and flipping back to a
    /// spinner would hide that.
    fn phase(&self, app: &AppHandle, phase: &str, detail: &str) {
        self.log(&format!("[{phase}] {detail}"));
        {
            let mut status = self.status.lock().unwrap();
            status.detail = detail.to_string();
            if status.state != "failed" {
                status.phase = phase.to_string();
            }
        }
        self.push(app);
    }

    /// Give up waiting and explain why. Does not kill the sidecar: if it recovers
    /// on its own the reader still navigates and the failure screen disappears.
    fn fail(&self, app: &AppHandle, phase: &str, detail: &str, hint: &str) {
        self.log(&format!("FAILED [{phase}] {detail}"));
        {
            let mut status = self.status.lock().unwrap();
            status.state = "failed";
            status.phase = phase.to_string();
            status.detail = detail.to_string();
            status.hint = hint.to_string();
        }
        self.push(app);
    }

    /// Send the current status to the splash. The page may not have run its script
    /// yet (the sidecar is spawned from `setup`, before the webview finishes
    /// loading), so the payload parks itself on `window.__tapPending` when
    /// `__tapApply` isn't defined and `splash.js` drains it on load.
    fn push(&self, app: &AppHandle) {
        let payload = {
            let mut status = self.status.lock().unwrap();
            status.elapsed_ms = self.elapsed().as_millis() as u64;
            status.slow = self.elapsed() >= SLOW_START_AFTER;
            status.log_path = self.log_path.to_string_lossy().to_string();
            status.log_tail = self.tail.lock().unwrap().iter().cloned().collect();
            status.clone()
        };
        let Ok(json) = serde_json::to_string(&payload) else {
            return;
        };
        if let Some(window) = app.get_webview_window("main") {
            // Same rule as the deep-link handler: the value is spliced into JS
            // source, so it goes in as a serde-produced literal, never as
            // hand-quoted text.
            let _ = window.eval(&format!(
                "(function(s){{if(window.__tapApply){{window.__tapApply(s)}}else{{window.__tapPending=s}}}})({json})"
            ));
        }
    }

    /// Point the webview at the running backend. Idempotent — the first caller wins.
    fn navigate(&self, app: &AppHandle, url: &str) {
        let Ok(parsed) = Url::parse(url) else {
            self.fail(
                app,
                "The Studio service reported an unusable address",
                &format!("`{url}` is not a URL the shell can navigate to."),
                "This is a bug — please attach the log below to a report.",
            );
            return;
        };

        // Look through the door before walking through it. Navigating is a one-way
        // door: it destroys the splash, and with it the only surface that can explain
        // a failure. See `probe_document`.
        self.phase(
            app,
            "Checking the Studio UI",
            &format!("Asking {parsed} for its start page"),
        );
        let deadline = Instant::now() + PROBE_GRACE;
        let mut attempts = 0u32;
        let why = loop {
            match probe_document(&parsed) {
                Ok(summary) => {
                    self.log(&format!("probe ok after {attempts} retries: {summary}"));
                    break None;
                }
                Err(e) if Instant::now() < deadline => {
                    // Only the first failure is written down. The retries are all the
                    // same sentence, and the log tail this ends up on is 60 lines that
                    // the sidecar's own output has a better claim to.
                    if attempts == 0 {
                        self.log(&format!(
                            "probe failed ({e}) — retrying for {}s",
                            PROBE_GRACE.as_secs()
                        ));
                    }
                    attempts += 1;
                    std::thread::sleep(Duration::from_millis(400));
                }
                Err(e) => break Some(e),
            }
        };
        if let Some(why) = why {
            self.fail(
                app,
                "The Studio UI could not be loaded",
                &format!(
                    "The service announced {parsed}, but it did not serve a usable page \
                     (after {attempts} retries over {}s): {why}",
                    PROBE_GRACE.as_secs()
                ),
                "The backend started, so this is almost always a broken or incomplete \
                 install — reinstall Tap Studio. The startup log is below.",
            );
            return;
        }

        if self.navigated.swap(true, Ordering::SeqCst) {
            return;
        }
        *app.state::<StudioApi>().0.lock().unwrap() = Some(url.to_string());
        if let Some(window) = app.get_webview_window("main") {
            // Drive the webview through `navigate` instead of eval'ing a
            // `location.replace('…')` literal: splicing a URL into JS source is an
            // injection sink the moment that URL stops being trusted.
            let _ = window.navigate(parsed);
            let _ = window.set_title("Tap Studio");
            let _ = window.emit("studio:ready", url);
        }
        self.log(&format!(
            "ready after {}ms at {url}",
            self.elapsed().as_millis()
        ));
    }

    /// Take the window back from a page that cannot show anything and put the
    /// splash's failure card on it.
    ///
    /// Without this every post-navigation failure — a workbench that renders
    /// nothing, a sidecar that dies mid-load — is a white window with no way back
    /// and nothing written down. `navigated` is cleared so the splash's Retry
    /// button works and so a recovered sidecar can navigate again.
    fn recover(&self, app: &AppHandle, phase: &str, detail: &str, hint: &str) {
        let home = self.splash_url.lock().unwrap().clone();
        self.navigated.store(false, Ordering::SeqCst);
        // Set the failure first: the push below may land on a page that is already
        // navigating away, and the splash re-pushes from its own load handler.
        self.fail(app, phase, detail, hint);
        match (home, app.get_webview_window("main")) {
            (Some(home), Some(window)) => match Url::parse(&home) {
                Ok(parsed) => {
                    let _ = window.navigate(parsed);
                }
                Err(e) => self.log(&format!("cannot return to the splash at {home}: {e}")),
            },
            _ => self.log("cannot return to the splash — its URL was never observed"),
        }
    }
}

/// Open this session's log, rotating the previous one to `studio.prev.log` so a
/// "it worked yesterday" comparison is still on disk. Returns the path either way —
/// it is shown on the failure screen even when the file could not be opened.
fn open_session_log(app: &AppHandle) -> (PathBuf, Option<File>) {
    let Ok(dir) = app.path().app_log_dir() else {
        return (PathBuf::from("<unavailable>"), None);
    };
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("studio.log");
    let _ = std::fs::rename(&path, dir.join("studio.prev.log"));
    let file = OpenOptions::new()
        .create(true)
        .write(true)
        .truncate(true)
        .open(&path)
        .ok();
    (path, file)
}

/// The deadline for the `studio.ready` handshake. `TAP_STUDIO_STARTUP_TIMEOUT_SECS=0`
/// disables it for anyone debugging a genuinely slow backend.
fn startup_timeout() -> Option<Duration> {
    match std::env::var("TAP_STUDIO_STARTUP_TIMEOUT_SECS")
        .ok()
        .and_then(|v| v.trim().parse::<u64>().ok())
    {
        Some(0) => None,
        Some(secs) => Some(Duration::from_secs(secs)),
        None => Some(DEFAULT_STARTUP_TIMEOUT),
    }
}

/// Open a URL in the user's default system browser. The Studio UI calls this for
/// OAuth sign-in: a webview popup can't drive an external login, so the authorize
/// URL is handed off to the real browser and the flow completes via the callback +
/// the UI's token polling.
#[tauri::command]
fn open_external(app: tauri::AppHandle, url: String) -> Result<(), String> {
    use tauri_plugin_opener::OpenerExt;
    // The webview is same-origin with an unauthenticated local API, so anything
    // that injects script into it can call this. Without a scheme check the
    // command is a general-purpose launcher for `file:`, `smb:`, and every
    // app-registered scheme on the machine — a much larger surface than the
    // opener plugin's own ACL would ever grant.
    let parsed = Url::parse(&url).map_err(|e| e.to_string())?;
    if !matches!(parsed.scheme(), "http" | "https") {
        return Err(format!(
            "refusing to open URL with scheme `{}` — only http and https are allowed",
            parsed.scheme()
        ));
    }
    app.opener()
        .open_url(url, None::<String>)
        .map_err(|e| e.to_string())
}

/// Splash "Try again" button: drop the current sidecar and start a fresh one.
/// Reachable only from the local splash origin (`capabilities/splash.json`) —
/// the sidecar-origin SPA must not be able to fork extra backends.
#[tauri::command]
fn studio_retry(app: tauri::AppHandle) -> Result<(), String> {
    let startup = app.state::<Arc<Startup>>().inner().clone();
    if let Some(child) = startup.child.lock().unwrap().take() {
        let _ = child.kill();
    }
    let attempt = startup.attempt.fetch_add(1, Ordering::SeqCst) + 1;
    *startup.attempt_started.lock().unwrap() = Instant::now();
    *startup.status.lock().unwrap() = Status::starting();
    // A retry reached from a *recovered* failure starts with the handover already
    // done; without this the fresh sidecar's `ready` is dropped as a duplicate and
    // the splash spins forever.
    startup.navigated.store(false, Ordering::SeqCst);
    startup.log(&format!("--- retry (attempt {}) ---", attempt + 1));
    // Off the event loop: a sync command body runs on the main thread and the
    // respawn re-runs the login-shell PATH probe, which is allowed to take seconds.
    std::thread::spawn(move || spawn_sidecar(&app, attempt));
    Ok(())
}

/// Splash "Show log" button: reveal the session log in Finder / Explorer / the
/// desktop file manager, so the user can attach it to a bug report.
#[tauri::command]
fn studio_open_log(app: tauri::AppHandle) -> Result<(), String> {
    use tauri_plugin_opener::OpenerExt;
    let path = app.state::<Arc<Startup>>().log_path.clone();
    app.opener()
        .reveal_item_in_dir(&path)
        .map_err(|e| e.to_string())
}

/// Query parameters `/api/auth/callback` actually reads (see
/// `Tap.Studio/Endpoints/AuthFlowEndpoints.cs`). A deep link's query is rebuilt
/// from exactly these keys rather than forwarded verbatim.
const AUTH_CALLBACK_PARAMS: [&str; 4] = ["code", "state", "error", "error_description"];

/// Map a `tap-studio://callback?…` deep link onto the sidecar's
/// `/api/auth/callback`, or return `None` when the link is not an auth callback.
///
/// Any web page can navigate the OS to `tap-studio://…`, so the link is hostile
/// input. The route is checked, and the outgoing query is rebuilt from an
/// allowlist so that `Url`'s own encoder — not the attacker — decides what the
/// value bytes look like by the time the URL reaches the webview.
fn auth_callback_target(api: &str, link: &Url) -> Option<Url> {
    if link.scheme() != "tap-studio" {
        return None;
    }
    // `tap-studio://callback` puts the route in the authority; the bare
    // `tap-studio:callback` form makes it an opaque path instead.
    let route = match (link.host_str(), link.path().trim_matches('/')) {
        (Some(host), "") => host,
        (None, path) => path,
        _ => return None,
    };
    if route != "callback" {
        return None;
    }

    let mut target = Url::parse(api).ok()?;
    if !matches!(target.scheme(), "http" | "https") {
        return None;
    }
    target.set_path("/api/auth/callback");

    let mut forwarded = 0usize;
    {
        let mut query = target.query_pairs_mut();
        query.clear();
        for (key, value) in link.query_pairs() {
            if AUTH_CALLBACK_PARAMS.contains(&&*key) {
                query.append_pair(&key, &value);
                forwarded += 1;
            }
        }
    }
    // No recognised parameter means this was not an OAuth redirect.
    (forwarded > 0).then_some(target)
}

/// Aspire dev path: the studio-api / studio-ui are already running, so point the
/// webview at the URL Aspire passed via `STUDIO_DESKTOP_URL` and record it for the
/// deep-link handler. No sidecar is spawned.
fn attach_external(app: &AppHandle, url: String) {
    let startup = app.state::<Arc<Startup>>().inner().clone();
    if Url::parse(&url).is_err() {
        startup.fail(
            app,
            "Bad STUDIO_DESKTOP_URL",
            &format!("`{url}` is not a valid URL, so there is nothing to attach to."),
            "Unset STUDIO_DESKTOP_URL to run the bundled sidecar instead.",
        );
        return;
    }
    startup.log(&format!("attaching to external backend at {url}"));
    startup.navigate(app, &url);
}

/// A macOS/Linux app launched from Finder/Dock inherits only a minimal PATH
/// (`/usr/bin:/bin:...`) — it never sources the user's shell — so the .NET sidecar
/// can't find user-installed CLIs like `az`, `gh`, `tailscale`, or `cloudflared`.
/// Resolve the real PATH from a login+interactive shell so those exec/auth
/// integrations work in the packaged app. Returns None on Windows or on any
/// failure, in which case the inherited PATH is left as-is.
#[cfg(not(target_os = "windows"))]
fn resolve_user_path() -> Option<String> {
    let shell = std::env::var("SHELL").ok()?;
    // Delimit the value so any interactive-shell banner/noise is stripped.
    let script = "printf '__TAP_PATH__%s__TAP_PATH__' \"$PATH\"";
    let out = std::process::Command::new(shell)
        .args(["-ilc", script])
        .output()
        .ok()?;
    let s = String::from_utf8_lossy(&out.stdout);
    let start = s.find("__TAP_PATH__")? + "__TAP_PATH__".len();
    let rest = &s[start..];
    let end = rest.find("__TAP_PATH__")?;
    let path = rest[..end].trim().to_string();
    (!path.is_empty()).then_some(path)
}

#[cfg(target_os = "windows")]
fn resolve_user_path() -> Option<String> {
    None
}

/// [`resolve_user_path`] under a deadline. It runs the user's *interactive* shell,
/// which is arbitrary code: an rc file that waits on a prompt, a stalled network
/// mount, or a slow version-manager init hangs it indefinitely. Losing the enriched
/// PATH costs a few CLI integrations; hanging here costs the whole app, on a splash
/// screen that cannot say why. The probe thread is left to finish on its own.
fn resolve_user_path_timely(limit: Duration) -> Result<Option<String>, ()> {
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(resolve_user_path());
    });
    rx.recv_timeout(limit).map_err(|_| ())
}

/// Fetch the document the webview is about to be pointed at, and decide whether it
/// is one that can render.
///
/// `studio.ready` only means Kestrel bound a port. The window, though, is handed
/// over exactly once: after `navigate` the splash — and with it every diagnostic the
/// user could have read — is gone. So a `ready` followed by a 404 (an install whose
/// `wwwroot` never arrived), a refused connection (a sidecar that died between
/// binding and serving), or a non-HTML body is a permanently white window with
/// nothing to read. One GET closes that gap while the splash is still on screen and
/// can still explain itself.
///
/// Hand-rolled rather than pulling in an HTTP client: this is a single
/// unauthenticated GET to loopback, and what we need back from it is the status line.
fn probe_document(url: &Url) -> Result<String, String> {
    // Only loopback http is probed. A dev `STUDIO_DESKTOP_URL` may be https or point
    // at a Vite server; there is no white-screen guarantee to enforce there, and a
    // hand-rolled client has no business speaking TLS.
    if url.scheme() != "http" {
        return Ok(format!("skipped ({} is not plain http)", url.scheme()));
    }
    let host = url.host_str().unwrap_or("127.0.0.1").to_string();
    let port = url.port_or_known_default().unwrap_or(80);
    let path = match url.path() {
        "" => "/",
        p => p,
    };

    // Every resolved address is tried, not just the first. On Windows `localhost`
    // resolves to `::1` ahead of `127.0.0.1`, and Kestrel bound to one of them — a
    // probe that gave up on the first refusal would report a working server as dead.
    let addrs = (host.as_str(), port)
        .to_socket_addrs()
        .map_err(|e| format!("{host}:{port} could not be resolved: {e}"))?
        .collect::<Vec<_>>();
    if addrs.is_empty() {
        return Err(format!("{host}:{port} resolved to no address."));
    }

    let mut failures = Vec::new();
    for addr in &addrs {
        match probe_addr(*addr, &host, port, path) {
            Ok(summary) => return Ok(summary),
            Err(e) => failures.push(format!("{addr}: {e}")),
        }
    }
    Err(failures.join("; "))
}

fn probe_addr(
    addr: std::net::SocketAddr,
    host: &str,
    port: u16,
    path: &str,
) -> Result<String, String> {
    let mut stream =
        TcpStream::connect_timeout(&addr, PROBE_TIMEOUT).map_err(|e| format!("connect: {e}"))?;
    let _ = stream.set_read_timeout(Some(PROBE_TIMEOUT));
    let _ = stream.set_write_timeout(Some(PROBE_TIMEOUT));

    // `Connection: close` so the read ends at EOF instead of on a chunk boundary we
    // would otherwise have to decode. The path comes from a URL the sidecar printed,
    // and a header-splitting `\r\n` in it would forge a second request — reject
    // rather than sanitize, since a legitimate path never contains one.
    if path.contains(['\r', '\n', ' ']) {
        return Err("the URL path contains control characters".into());
    }
    let request = format!(
        "GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nAccept: text/html\r\n\
         User-Agent: tap-studio-shell\r\nConnection: close\r\n\r\n"
    );
    stream
        .write_all(request.as_bytes())
        .map_err(|e| format!("write: {e}"))?;

    let mut buf = Vec::with_capacity(4096);
    let mut chunk = [0u8; 4096];
    loop {
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                buf.extend_from_slice(&chunk[..n]);
                if buf.len() >= PROBE_MAX_BYTES {
                    break;
                }
            }
            Err(e) => {
                if buf.is_empty() {
                    return Err(format!("read: {e}"));
                }
                break;
            }
        }
    }

    let text = String::from_utf8_lossy(&buf);
    let (head, body) = match text.find("\r\n\r\n") {
        Some(i) => (&text[..i], &text[i + 4..]),
        None => (text.as_ref(), ""),
    };
    let status_line = head.lines().next().unwrap_or("").trim().to_string();
    let code = status_line
        .split_whitespace()
        .nth(1)
        .and_then(|c| c.parse::<u16>().ok())
        .ok_or_else(|| format!("unreadable response: {status_line:?}"))?;

    if !(200..300).contains(&code) {
        // A 404 here is the empty-wwwroot case, which is exactly the failure this
        // probe exists to name. Say the code out loud rather than "could not load".
        return Err(format!(
            "the server answered {code} for {path}{}",
            if code == 404 {
                " — the UI files are not installed"
            } else {
                ""
            }
        ));
    }
    if body.trim().is_empty() {
        return Err(format!("the server answered {code} with an empty body"));
    }
    Ok(format!("{status_line} ({} bytes)", body.len()))
}

/// The watchdog the shell injects into the workbench page once it has loaded.
///
/// A document that parses, answers 200, and then renders nothing — a bundle whose
/// top-level code throws, a WebView2 too old for the syntax Vite emitted — is the
/// one white screen no server-side check can catch. So the page watches itself: it
/// records what its own error handlers see, and if nothing has painted by the
/// deadline it hands the window back to the splash with the reason in the fragment.
///
/// `{home}` is the splash URL and `{ms}` the deadline; both are spliced in as
/// serde-produced JSON literals, never as hand-quoted text.
const BLANK_WATCHDOG_JS: &str = r#"
(function () {
  if (window.__tapBlankWatch) return;
  window.__tapBlankWatch = 1;
  var home = __HOME__, deadline = __MS__, seen = [];
  function note(m) {
    m = String(m);
    if (seen.length < 6 && seen.indexOf(m) === -1) seen.push(m);
  }
  // Capture phase so failed subresource loads (the module bundle itself) are seen,
  // not just errors that bubble.
  window.addEventListener('error', function (e) {
    if (e && e.target && e.target !== window && e.target.src) {
      note('failed to load ' + e.target.src);
    } else if (e) {
      note((e.message || 'script error') + (e.filename ? ' @ ' + e.filename + ':' + e.lineno : ''));
    }
  }, true);
  window.addEventListener('unhandledrejection', function (e) {
    var r = e && e.reason;
    note('unhandled rejection: ' + ((r && (r.stack || r.message)) || r));
  });
  setTimeout(function () {
    var root = document.getElementById('root');
    var painted = root ? root.childElementCount > 0
                       : !!(document.body && document.body.childElementCount > 0);
    if (painted) return;
    var why = seen.length
      ? seen.join(' | ')
      : 'The page loaded but rendered nothing, and reported no error of its own.';
    try {
      location.replace(home + '#__KEY__=' + encodeURIComponent(why));
    } catch (err) {
      document.title = 'tap-blank';
    }
  }, deadline);
})();
"#;

/// The blank-page deadline. `TAP_STUDIO_BLANK_TIMEOUT_SECS=0` turns the watchdog
/// off for anyone deliberately staring at a slow first render.
fn blank_timeout() -> Option<Duration> {
    match std::env::var("TAP_STUDIO_BLANK_TIMEOUT_SECS")
        .ok()
        .and_then(|v| v.trim().parse::<u64>().ok())
    {
        Some(0) => None,
        Some(secs) => Some(Duration::from_secs(secs)),
        None => Some(DEFAULT_BLANK_TIMEOUT),
    }
}

/// Pull the watchdog's diagnosis out of a splash URL it navigated back to.
fn reported_failure(url: &Url) -> Option<String> {
    let fragment = url.fragment()?;
    url::form_urlencoded::parse(fragment.as_bytes())
        .find(|(k, _)| k == FAIL_FRAGMENT_KEY)
        .map(|(_, v)| {
            let mut text = v.into_owned();
            text.truncate(MAX_REPORTED_ERROR);
            text
        })
}

/// Packaged path: spawn the bundled, self-contained Tap.Studio sidecar and drive
/// the webview from its `studio.ready` stdout handshake.
fn spawn_sidecar(app: &AppHandle, attempt: u64) {
    let startup = app.state::<Arc<Startup>>().inner().clone();

    // The sidecar is configured with Studio:Port=0 so Kestrel asks the OS for a
    // free port — avoids collisions with any running dev instance — and
    // TAP_STUDIO_EMIT_READY=1 so the host echoes JSON progress lines on stdout,
    // ending with the one that carries the bound URL.
    let sidecar = match app.shell().sidecar("tap-studio") {
        Ok(cmd) => cmd,
        Err(e) => {
            startup.fail(
                app,
                "Studio service missing",
                &format!("The bundled tap-studio binary could not be located: {e}"),
                "The app bundle looks incomplete — reinstall Tap Studio.",
            );
            return;
        }
    };

    // The Studio binary is published with PublishSingleFile, so its
    // AppContext.BaseDirectory points at a temp extraction dir, not at wwwroot.
    // The build script ships wwwroot as a bundle resource — resolve its absolute
    // path now and forward it to Kestrel via Studio__WebRoot. In dev
    // (BaseDirectory::Resource) maps to src-tauri/, so the path becomes
    // src-tauri/binaries/wwwroot.
    let web_root = app
        .path()
        .resolve("binaries/wwwroot", tauri::path::BaseDirectory::Resource)
        .ok()
        .map(|p| p.to_string_lossy().to_string());
    match &web_root {
        Some(root) => startup.log(&format!("wwwroot resource resolved to {root}")),
        // Launching anyway used to mean a guaranteed white screen: the sidecar would
        // bind, report ready, and serve 404 for `/`. Say so here, while the splash is
        // still the thing on screen.
        None => {
            startup.fail(
                app,
                "The Studio UI is missing",
                "The bundled UI files (binaries/wwwroot) could not be located inside the \
                 app's resources, so there is nothing to display.",
                "The install is incomplete — reinstall Tap Studio.",
            );
            return;
        }
    }

    let mut cmd = sidecar
        .env("TAP_STUDIO_EMIT_READY", "1")
        .env("TAP_STUDIO_DESKTOP", "1")
        .env("Studio__Port", "0")
        .env("Studio__Host", "localhost");
    if let Some(root) = &web_root {
        cmd = cmd.env("Studio__WebRoot", root);
    }

    // Give the sidecar the user's real PATH so `az`, `gh`, etc. resolve.
    startup.phase(app, "Reading shell environment", "Resolving your login PATH");
    match resolve_user_path_timely(PATH_PROBE_TIMEOUT) {
        Ok(Some(path)) => cmd = cmd.env("PATH", path),
        Ok(None) => startup.log("login-shell PATH probe returned nothing — using the inherited PATH"),
        Err(()) => startup.log(&format!(
            "login-shell PATH probe did not answer within {}s — using the inherited PATH. \
             CLI-backed features (az, gh, op, …) may not find their binaries.",
            PATH_PROBE_TIMEOUT.as_secs()
        )),
    }

    startup.phase(app, "Starting the Studio service", "Spawning the sidecar process");
    let (mut rx, child) = match cmd.spawn() {
        Ok(pair) => pair,
        Err(e) => {
            startup.fail(
                app,
                "Studio service would not start",
                &format!("Spawning the tap-studio sidecar failed: {e}"),
                "On macOS this is usually Gatekeeper quarantine on a downloaded build — \
                 reinstall from the .dmg, or run `xattr -dr com.apple.quarantine \
                 /Applications/Tap\\ Studio.app`.",
            );
            return;
        }
    };
    startup.log(&format!("sidecar pid {}", child.pid()));
    *startup.child.lock().unwrap() = Some(child);
    startup.phase(
        app,
        "Waiting for the Studio service",
        "The service is starting up",
    );

    watch_startup(app.clone(), startup.clone(), attempt);

    // Drain the sidecar's output. Structured lines drive the splash; everything
    // else is logged so a failed launch has context to show.
    let handle = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            // A retry supersedes this attempt: keep logging (the old process may
            // still have something useful to say) but stop touching the UI.
            let current = startup.attempt() == attempt;
            match event {
                CommandEvent::Stdout(bytes) => {
                    let line = String::from_utf8_lossy(&bytes);
                    let trimmed = line.trim();
                    if trimmed.is_empty() {
                        continue;
                    }
                    match serde_json::from_str::<SidecarEvent>(trimmed) {
                        Ok(SidecarEvent::Ready { url }) => {
                            if current {
                                startup.navigate(&handle, &url);
                            }
                        }
                        Ok(SidecarEvent::Progress { phase, message }) => {
                            startup.log(&format!("sidecar {phase}: {message}"));
                            if current && !startup.navigated.load(Ordering::SeqCst) {
                                startup.phase(&handle, "Waiting for the Studio service", &message);
                            }
                        }
                        Ok(SidecarEvent::Error { phase, message }) => {
                            if current {
                                startup.fail(
                                    &handle,
                                    "The Studio service reported an error",
                                    &format!("{phase}: {message}"),
                                    "The full startup log is below.",
                                );
                            } else {
                                startup.log(&format!("sidecar error {phase}: {message}"));
                            }
                        }
                        Err(_) => startup.log(&format!("sidecar: {trimmed}")),
                    }
                }
                CommandEvent::Stderr(bytes) => {
                    let text = String::from_utf8_lossy(&bytes);
                    let trimmed = text.trim();
                    if !trimmed.is_empty() {
                        startup.log(&format!("sidecar!: {trimmed}"));
                    }
                }
                CommandEvent::Error(err) => {
                    startup.log(&format!("sidecar io error: {err}"));
                }
                CommandEvent::Terminated(payload) => {
                    startup.log(&format!(
                        "sidecar exited: code={:?} signal={:?}",
                        payload.code, payload.signal
                    ));
                    if current {
                        let code = payload
                            .code
                            .map(|c| format!("exit code {c}"))
                            .or_else(|| payload.signal.map(|s| format!("signal {s}")))
                            .unwrap_or_else(|| "no exit code".into());
                        let hint = "The last lines of its output are below — they normally name \
                                    the cause (a port it could not bind, a workspace it could not \
                                    read, a missing runtime file).";
                        if startup.navigated.load(Ordering::SeqCst) {
                            // Death *after* the handover. The webview is sitting on a page
                            // whose backend has gone — blank if it died mid-load, frozen if
                            // it died later. Either way the window has to come back to a
                            // surface that can say what happened.
                            startup.recover(
                                &handle,
                                "The Studio service stopped",
                                &format!("The backend quit while the app was running ({code})."),
                                hint,
                            );
                        } else {
                            startup.fail(
                                &handle,
                                "The Studio service stopped",
                                &format!(
                                    "The backend quit after {}ms ({code}) without reporting that it was ready.",
                                    startup.elapsed().as_millis()
                                ),
                                hint,
                            );
                        }
                    }
                    break;
                }
                _ => {}
            }
        }
    });
}

/// Ticks the splash's elapsed counter and enforces the handshake deadline. Runs on
/// a plain thread — the reader task is busy on the sidecar's stdout, and a watchdog
/// that shares its fate would be no watchdog at all.
fn watch_startup(app: AppHandle, startup: Arc<Startup>, attempt: u64) {
    let timeout = startup_timeout();
    std::thread::spawn(move || {
        let mut timed_out = false;
        loop {
            std::thread::sleep(Duration::from_millis(500));
            if startup.navigated.load(Ordering::SeqCst) || startup.attempt() != attempt {
                return;
            }
            let elapsed = startup.elapsed();
            if !timed_out {
                if let Some(limit) = timeout {
                    if elapsed >= limit {
                        timed_out = true;
                        let detail = startup.status.lock().unwrap().detail.clone();
                        startup.fail(
                            &app,
                            "Tap Studio is taking too long to start",
                            &format!(
                                "The backend has not reported that it is ready after {}s. \
                                 Last step: {}",
                                elapsed.as_secs(),
                                if detail.is_empty() { "spawning the sidecar" } else { detail.as_str() }
                            ),
                            "It is still running, so it may just be slow — a workspace folder with a \
                             very large number of files takes a while to scan the first time. \
                             Leave it a moment, or try again.",
                        );
                        continue;
                    }
                }
            }
            // Keep the counter (and, past the slow mark, the detail line) live.
            startup.push(&app);
        }
    });
}

/// Every navigation the main webview makes, and the only place the shell learns
/// that a page has actually finished loading.
///
/// Three jobs, all of them about not going white:
///  1. Remember the splash's own URL the first time it loads, so [`Startup::recover`]
///     has somewhere to navigate back to.
///  2. Re-push the current status when the splash finishes loading. `push` evals into
///     whatever page is current, so a status set while the splash was still loading
///     (or while the window was on its way back from a dead workbench) would
///     otherwise be written into a page that no longer exists.
///  3. Arm [`BLANK_WATCHDOG_JS`] on the workbench page, and render whatever it
///     reports back.
fn on_page_load(webview: &tauri::Webview, payload: &tauri::webview::PageLoadPayload<'_>) {
    use tauri::webview::PageLoadEvent;

    let app = webview.app_handle().clone();
    let Some(startup) = app.try_state::<Arc<Startup>>().map(|s| s.inner().clone()) else {
        return;
    };
    let url = payload.url().clone();
    let finished = matches!(payload.event(), PageLoadEvent::Finished);
    startup.log(&format!(
        "webview {} {url}",
        if finished { "loaded" } else { "loading" }
    ));

    // Both surfaces are matched by origin, not by whole URL, so the SPA's own in-app
    // routing and the splash's `#tapfail=` fragment stay on the right side. A page
    // that is neither — anything the workbench navigated itself to — is logged and
    // otherwise left alone: treating it as the splash would let it overwrite the
    // address `recover` navigates back to.
    let api = app.state::<StudioApi>().0.lock().unwrap().clone();
    let is_workbench = api
        .as_deref()
        .and_then(|a| Url::parse(a).ok())
        .is_some_and(|a| a.origin() == url.origin());
    let is_splash = startup
        .splash_url
        .lock()
        .unwrap()
        .as_deref()
        .and_then(|u| Url::parse(u).ok())
        .is_some_and(|u| u.origin() == url.origin());

    if is_workbench {
        // Armed on *both* events on purpose. The failure this exists to catch is a
        // bundle that throws while it evaluates, and a sink installed at `Finished`
        // registers after that has already happened — so try `Started` first, which
        // lands before the document's own scripts run. The script guards itself
        // against double-arming, and an early eval that misses the new document (or
        // hits the outgoing one) is harmless: `Finished` still arms the right page.
        arm_blank_watchdog(webview, &startup);
        return;
    }

    if !is_splash || !finished {
        return;
    }

    // Back on the splash. If the workbench sent us here, it said why in the fragment.
    if let Some(why) = reported_failure(&url) {
        startup.navigated.store(false, Ordering::SeqCst);
        startup.fail(
            &app,
            "The Studio UI loaded but did not render",
            &why,
            "The service itself is running — this is the workbench page failing in the \
             webview. On Windows that is usually an out-of-date WebView2 runtime; \
             updating Microsoft Edge WebView2 and relaunching normally fixes it.",
        );
        return;
    }
    startup.push(&app);
}

/// Drop the fragment from an observed URL. The splash address is reused as a
/// navigation target, and carrying a stale `#tapfail=…` into it would re-raise a
/// failure the user has already dismissed by retrying.
fn strip_fragment(url: &Url) -> String {
    let mut clean = url.clone();
    clean.set_fragment(None);
    clean.to_string()
}

/// Inject the self-watching script into the freshly loaded workbench page.
fn arm_blank_watchdog(webview: &tauri::Webview, startup: &Arc<Startup>) {
    let Some(limit) = blank_timeout() else {
        startup.log("blank-page watchdog disabled by TAP_STUDIO_BLANK_TIMEOUT_SECS=0");
        return;
    };
    let home = startup.splash_url.lock().unwrap().clone();
    let Some(home) = home else {
        startup.log("blank-page watchdog not armed — the splash URL was never observed");
        return;
    };
    // Both values are spliced into JS source, so both go in as serde-produced
    // literals. `home` in particular is a URL, and a URL in hand-quoted source is an
    // injection sink the moment it stops being one we minted.
    let Ok(home_literal) = serde_json::to_string(&home) else {
        return;
    };
    let script = BLANK_WATCHDOG_JS
        .replace("__HOME__", &home_literal)
        .replace("__MS__", &limit.as_millis().to_string())
        .replace("__KEY__", FAIL_FRAGMENT_KEY);
    match webview.eval(&script) {
        Ok(()) => startup.log(&format!("blank-page watchdog armed ({}s)", limit.as_secs())),
        Err(e) => startup.log(&format!("could not arm the blank-page watchdog: {e}")),
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .on_page_load(on_page_load)
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_deep_link::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![
            check_for_update,
            install_update,
            open_external,
            studio_retry,
            studio_open_log
        ])
        .manage(StudioApi::default())
        .setup(|app| {
            let handle = app.handle().clone();
            let (log_path, log) = open_session_log(&handle);
            let startup = Arc::new(Startup::new(log_path, log));
            let epoch = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|d| d.as_secs())
                .unwrap_or_default();
            startup.log(&format!(
                "Tap Studio {} starting on {}/{} (unix time {epoch})",
                app.package_info().version,
                std::env::consts::OS,
                std::env::consts::ARCH
            ));
            // The webview runtime, not the app, is what renders the workbench — and on
            // Windows an out-of-date WebView2 is the classic cause of a page that loads
            // and then shows nothing. Put its version in the log before anything else
            // can go wrong.
            match tauri::webview_version() {
                Ok(v) => startup.log(&format!("webview runtime {v}")),
                Err(e) => startup.log(&format!("webview runtime version unavailable: {e}")),
            }

            // Read the splash address from the window itself rather than waiting to
            // observe its first load. `navigate` can win that race — it always does
            // in the `STUDIO_DESKTOP_URL` path, which runs straight from here — and a
            // shell that never learned where its splash lives has no way back from a
            // workbench that turns out to be blank.
            match handle
                .get_webview_window("main")
                .ok_or_else(|| "no main window".to_string())
                .and_then(|w| w.url().map_err(|e| e.to_string()))
            {
                Ok(url) => {
                    startup.log(&format!("splash at {url}"));
                    *startup.splash_url.lock().unwrap() = Some(strip_fragment(&url));
                }
                Err(e) => startup.log(&format!(
                    "could not read the splash URL ({e}) — the blank-page watchdog will not arm"
                )),
            }
            app.manage(startup);

            // Decide the backend the webview talks to. Under Aspire dev the
            // studio-api / studio-ui are already running, so STUDIO_DESKTOP_URL
            // points the webview straight at them and we skip the bundled
            // sidecar. Packaged builds leave it unset and self-host the sidecar.
            match std::env::var("STUDIO_DESKTOP_URL")
                .ok()
                .filter(|u| !u.trim().is_empty())
            {
                Some(url) => attach_external(&handle, url),
                // Off the main thread: `setup` runs before the event loop, so anything
                // slow here (the login-shell PATH probe, a sidecar that takes its time
                // to answer) delays the window itself from appearing — and a window
                // that never appears is the one failure the splash cannot narrate.
                None => {
                    let spawn_handle = handle.clone();
                    std::thread::spawn(move || spawn_sidecar(&spawn_handle, 0));
                }
            }

            // Wire the OAuth deep-link callback. When the browser-side
            //    authorize redirect lands on tap-studio://callback?... we
            //    forward the query string to the in-process Studio's
            //    /api/auth/callback. The webview's same-origin SPA then sees
            //    the flow complete the same way the localhost-callback path
            //    works in dev.
            let cb_handle = app.handle().clone();
            app.deep_link().on_open_url(move |event| {
                let urls = event.urls();
                let Some(link) = urls.first() else { return };
                let Some(api) = cb_handle.state::<StudioApi>().0.lock().unwrap().clone() else {
                    eprintln!("[studio] deep link received before sidecar ready: {link}");
                    return;
                };
                let Some(target) = auth_callback_target(&api, link) else {
                    eprintln!("[studio] ignoring deep link that is not an auth callback: {link}");
                    return;
                };
                // Hand off to the webview — it has the cookies/state the
                // Studio callback page needs, and it's same-origin with the
                // sidecar so the request goes through cleanly. Avoids
                // pulling reqwest just to do a GET.
                if let Some(window) = cb_handle.get_webview_window("main") {
                    // Embed the URL as a JSON literal. The previous
                    // hand-rolled escape only handled `'`, so a deep link whose
                    // query ended in a backslash turned the escape into a
                    // literal `\` plus a *closing* quote and ran the rest of the
                    // query as script inside the Studio origin.
                    let Ok(literal) = serde_json::to_string(target.as_str()) else {
                        return;
                    };
                    let _ = window.eval(format!("fetch({literal})"));
                }
            });

            #[cfg(desktop)]
            {
                // Register the scheme so OS browsers know to open tap-studio://
                // URLs with this app even on the first launch. No-op when the
                // scheme is already registered.
                let _ = app.deep_link().register("tap-studio");
            }

            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building Tap Studio")
        .run(|app, event| {
            // Take the sidecar down with the window. Without this the .NET process
            // outlives a quit and the next launch starts alongside an orphan that
            // still holds the user's ~/.tap state files open.
            if let tauri::RunEvent::Exit = event {
                if let Some(startup) = app.try_state::<Arc<Startup>>() {
                    if let Some(child) = startup.child.lock().unwrap().take() {
                        let _ = child.kill();
                    }
                }
            }
        });
}
