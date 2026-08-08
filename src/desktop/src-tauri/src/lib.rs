use std::collections::VecDeque;
use std::fs::{File, OpenOptions};
use std::io::Write;
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
            self.log(&format!("unusable url from sidecar: {url}"));
            return;
        };
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
    if web_root.is_none() {
        startup.log("WARNING could not resolve the wwwroot resource — the UI will be blank");
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
                    if current && !startup.navigated.load(Ordering::SeqCst) {
                        let code = payload
                            .code
                            .map(|c| format!("exit code {c}"))
                            .or_else(|| payload.signal.map(|s| format!("signal {s}")))
                            .unwrap_or_else(|| "no exit code".into());
                        startup.fail(
                            &handle,
                            "The Studio service stopped",
                            &format!(
                                "The backend quit after {}ms ({code}) without reporting that it was ready.",
                                startup.elapsed().as_millis()
                            ),
                            "The last lines of its output are below — they normally name the cause \
                             (a port it could not bind, a workspace it could not read, a missing runtime file).",
                        );
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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
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
                "Tap Studio {} starting on {} (unix time {epoch})",
                app.package_info().version,
                std::env::consts::OS
            ));
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
