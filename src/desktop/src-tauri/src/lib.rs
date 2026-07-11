use std::sync::Mutex;

use serde::Deserialize;
use tauri::{Emitter, Manager};
use tauri_plugin_deep_link::DeepLinkExt;
use tauri_plugin_shell::process::CommandEvent;
use tauri_plugin_shell::ShellExt;
use tauri_plugin_updater::UpdaterExt;

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

#[derive(Deserialize)]
struct StudioReady {
    /// Always `studio.ready`. Kept so we can multiplex other events on the same
    /// stdout channel later (e.g. progress, warnings).
    #[serde(rename = "event")]
    _event: String,
    url: String,
}

/// Open a URL in the user's default system browser. The Studio UI calls this for
/// OAuth sign-in: a webview popup can't drive an external login, so the authorize
/// URL is handed off to the real browser and the flow completes via the callback +
/// the UI's token polling.
#[tauri::command]
fn open_external(app: tauri::AppHandle, url: String) -> Result<(), String> {
    use tauri_plugin_opener::OpenerExt;
    app.opener()
        .open_url(url, None::<String>)
        .map_err(|e| e.to_string())
}

/// Aspire dev path: the studio-api / studio-ui are already running, so point the
/// webview at the URL Aspire passed via `STUDIO_DESKTOP_URL` and record it for the
/// deep-link handler. No sidecar is spawned.
fn attach_external(app: &tauri::App, url: String) {
    *app.state::<StudioApi>().0.lock().unwrap() = Some(url.clone());
    if let Some(window) = app.get_webview_window("main") {
        let escaped = url.replace('\'', "\\'");
        let _ = window.eval(&format!("window.location.replace('{escaped}')"));
        let _ = window.set_title("Tap Studio");
        let _ = window.emit("studio:ready", &url);
    }
    eprintln!("[studio] attached to external backend at {url}");
}

/// Packaged path: spawn the bundled, self-contained Tap.Studio sidecar and drive
/// the webview from its `studio.ready` stdout handshake.
fn spawn_sidecar(app: &tauri::App) {
    // The sidecar is configured with Studio:Port=0 so Kestrel asks the OS for a
    // free port — avoids collisions with any running dev instance — and
    // TAP_STUDIO_EMIT_READY=1 so the host echoes a single JSON line on stdout once
    // Kestrel finishes binding.
    let sidecar = app
        .shell()
        .sidecar("tap-studio")
        .expect("sidecar binary missing — run scripts/build-desktop.sh first");

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
        eprintln!("[studio] could not resolve wwwroot resource — UI will be blank");
    }

    let mut cmd = sidecar
        .env("TAP_STUDIO_EMIT_READY", "1")
        .env("TAP_STUDIO_DESKTOP", "1")
        .env("Studio__Port", "0")
        .env("Studio__Host", "localhost");
    if let Some(root) = &web_root {
        cmd = cmd.env("Studio__WebRoot", root);
    }

    let (mut rx, _child) = cmd.spawn().expect("failed to spawn Tap.Studio sidecar");

    // Drain stdout. The first line that parses as StudioReady gives us the URL to
    // load. After that, lines are logged for triage.
    let handle = app.handle().clone();
    tauri::async_runtime::spawn(async move {
        let mut navigated = false;
        while let Some(event) = rx.recv().await {
            match event {
                CommandEvent::Stdout(bytes) => {
                    let line = String::from_utf8_lossy(&bytes);
                    let trimmed = line.trim();
                    if !navigated {
                        if let Ok(ready) = serde_json::from_str::<StudioReady>(trimmed) {
                            *handle.state::<StudioApi>().0.lock().unwrap() =
                                Some(ready.url.clone());
                            if let Some(window) = handle.get_webview_window("main") {
                                let escaped = ready.url.replace('\'', "\\'");
                                let _ =
                                    window.eval(&format!("window.location.replace('{escaped}')"));
                                let _ = window.set_title("Tap Studio");
                                let _ = window.emit("studio:ready", &ready.url);
                                navigated = true;
                            }
                            continue;
                        }
                    }
                    eprintln!("[studio] {trimmed}");
                }
                CommandEvent::Stderr(bytes) => {
                    eprintln!("[studio:err] {}", String::from_utf8_lossy(&bytes).trim());
                }
                CommandEvent::Terminated(payload) => {
                    eprintln!("[studio] sidecar exited: code={:?}", payload.code);
                }
                _ => {}
            }
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
            open_external
        ])
        .manage(StudioApi::default())
        .setup(|app| {
            // Decide the backend the webview talks to. Under Aspire dev the
            // studio-api / studio-ui are already running, so STUDIO_DESKTOP_URL
            // points the webview straight at them and we skip the bundled
            // sidecar. Packaged builds leave it unset and self-host the sidecar.
            match std::env::var("STUDIO_DESKTOP_URL")
                .ok()
                .filter(|u| !u.trim().is_empty())
            {
                Some(url) => attach_external(app, url),
                None => spawn_sidecar(app),
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
                let Some(url) = urls.first() else { return };
                let Some(api) = cb_handle.state::<StudioApi>().0.lock().unwrap().clone() else {
                    eprintln!("[studio] deep link received before sidecar ready: {url}");
                    return;
                };
                let query = url.query().unwrap_or("");
                let target = format!("{api}/api/auth/callback?{query}");
                // Hand off to the webview — it has the cookies/state the
                // Studio callback page needs, and it's same-origin with the
                // sidecar so the request goes through cleanly. Avoids
                // pulling reqwest just to do a GET.
                if let Some(window) = cb_handle.get_webview_window("main") {
                    let _ = window.eval(&format!("fetch('{}')", target.replace('\'', "\\'")));
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
        .run(tauri::generate_context!())
        .expect("error while running Tap Studio");
}
