fn main() {
    // Register the app's own commands with the ACL so Tauri generates
    // `allow-*` / `deny-*` permissions for them. Without this, app commands are
    // only reachable from the local (tauri://) context — and Tap Studio
    // redirects the webview to http://127.0.0.1:<port>, a *remote* context,
    // where every app command is ACL-denied unless explicitly granted in a
    // capability (see capabilities/default.json).
    tauri_build::try_build(
        tauri_build::Attributes::new().app_manifest(
            tauri_build::AppManifest::new().commands(&["check_for_update", "install_update"]),
        ),
    )
    .expect("failed to run tauri-build");
}
