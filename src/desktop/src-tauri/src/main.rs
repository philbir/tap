// Tauri shell binary. All logic lives in the library so it can be reused for
// mobile entry points later (see #[cfg_attr(mobile, tauri::mobile_entry_point)]
// inside the lib).
#![cfg_attr(
    all(not(debug_assertions), target_os = "windows"),
    windows_subsystem = "windows"
)]

fn main() {
    tap_studio_desktop_lib::run()
}
