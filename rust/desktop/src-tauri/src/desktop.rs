use net_drive_guard_app::{
    protocol::{self, Job},
    Session, Settings, Snapshot,
};
use net_drive_guard_core::{monitor::Action, schedule::Trigger};
use std::{
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Emitter, Manager,
};
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_notification::NotificationExt;
use zeroize::Zeroizing;
struct AppState {
    session: Mutex<Session>,
    cancel: AtomicBool,
    stop: AtomicBool,
    epoch: Instant,
}
type Shared = Arc<AppState>;
fn lock(state: &AppState) -> Result<std::sync::MutexGuard<'_, Session>, String> {
    state.session.lock().map_err(|_| "state_unavailable".into())
}
fn publish(app: &tauri::AppHandle, state: &AppState) {
    let (snapshot, notice) = match lock(state) {
        Ok(mut s) => (s.snapshot.clone(), s.notice.take()),
        Err(_) => return,
    };
    let _ = app.emit("state-changed", &snapshot);
    if let Some(tray) = app.tray_by_id("main-tray") {
        let label = state_text(&snapshot.state, &snapshot.settings.language);
        let _ = tray.set_tooltip(Some(format!("NetDriveGuard — {label}")));
        let _ = tray.set_icon(Some(icon(&snapshot.state)));
    }
    if let Some(notice) = notice {
        let body = state_text(&notice.state, &notice.language);
        if app
            .notification()
            .builder()
            .title("NetDriveGuard")
            .body(body)
            .show()
            .is_err()
        {
            if let Ok(mut s) = lock(state) {
                s.record("notification_failed", "warning", None);
            }
        }
    }
}
#[tauri::command]
fn get_snapshot(state: tauri::State<Shared>) -> Result<Snapshot, String> {
    Ok(lock(&state)?.snapshot.clone())
}
#[tauri::command]
fn get_events(
    state: tauri::State<Shared>,
) -> Result<std::collections::VecDeque<net_drive_guard_app::Event>, String> {
    Ok(lock(&state)?.events())
}
#[tauri::command]
fn save_settings(
    settings: Settings,
    directory: String,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<Snapshot, String> {
    let host = app.state::<crate::host::Shared>();
    let snapshot = crate::host::with_storage_change(&host, || {
        let mut s = lock(&state)?;
        let result = (|| {
            s.save(
                settings,
                if directory.is_empty() {
                    None
                } else {
                    Some(std::path::Path::new(&directory))
                },
                state.epoch.elapsed(),
            )?;
            let config = s.snapshot.settings.validate()?;
            s.snapshot.credential_saved = net_drive_guard_windows::credentials::read(&config)
                .map_err(|_| "credential_read_failed")?
                .is_some();
            Ok(s.snapshot.clone())
        })();
        Ok((result, PathBuf::from(&s.snapshot.data_directory)))
    })?;
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
fn relocate_data(
    directory: String,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<Snapshot, String> {
    let host = app.state::<crate::host::Shared>();
    let snapshot = crate::host::with_storage_change(&host, || {
        let mut s = lock(&state)?;
        let result = s.relocate(std::path::Path::new(&directory));
        Ok((result, PathBuf::from(&s.snapshot.data_directory)))
    })?;
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
fn save_preferences(
    language: String,
    notifications: bool,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<Snapshot, String> {
    let snapshot = lock(&state)?.save_preferences(net_drive_guard_app::settings::Preferences {
        language,
        notifications,
    })?;
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
fn check_now(state: tauri::State<Shared>, app: tauri::AppHandle) -> Result<Snapshot, String> {
    let snapshot = {
        let mut s = lock(&state)?;
        s.request_check()?;
        s.snapshot.clone()
    };
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
fn set_paused(
    paused: bool,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<Snapshot, String> {
    let snapshot = {
        let mut s = lock(&state)?;
        if paused {
            s.pause(state.epoch.elapsed());
            state.cancel.store(true, Ordering::Release);
        } else {
            s.resume(state.epoch.elapsed());
        }
        s.snapshot.clone()
    };
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
fn set_credential(
    user: String,
    password: String,
    remove: bool,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<Snapshot, String> {
    let password = Zeroizing::new(password);
    let user = Zeroizing::new(user);
    let snapshot = {
        let mut s = lock(&state)?;
        if s.snapshot.busy {
            return Err("busy".into());
        }
        if !s.snapshot.configured {
            return Err("not_configured".into());
        }
        let config = s.snapshot.settings.validate()?;
        if remove {
            net_drive_guard_windows::credentials::delete(&config)
                .map_err(|_| "credential_delete_failed")?;
        } else {
            net_drive_guard_windows::credentials::write(&config, &user, &password)
                .map_err(|_| "credential_write_failed")?;
        }
        s.snapshot.credential_saved = !remove;
        s.record(
            if remove {
                "credential_deleted"
            } else {
                "credential_saved"
            },
            "info",
            None,
        );
        if !s.snapshot.paused {
            s.requested = Some(Trigger::Manual);
        }
        s.snapshot.clone()
    };
    publish(&app, &state);
    Ok(snapshot)
}
async fn startup_operation(
    state: Shared,
    app: tauri::AppHandle,
    action: &'static str,
) -> Result<Snapshot, String> {
    let settings = {
        let mut s = lock(&state)?;
        if s.snapshot.busy {
            return Err("busy".into());
        }
        s.snapshot.busy = true;
        state.cancel.store(false, Ordering::Release);
        s.snapshot.settings.clone()
    };
    publish(&app, &state);
    let worker_state = state.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        protocol::run(
            &protocol::Request {
                settings,
                job: Job::Startup {
                    action: action.into(),
                },
            },
            Duration::from_secs(20),
            &worker_state.cancel,
        )
    })
    .await
    .map_err(|_| "worker_failed")
    .and_then(|v| v.map_err(|_| "startup_failed"));
    let snapshot = {
        let mut s = lock(&state)?;
        s.snapshot.busy = false;
        match result {
            Ok(reply) => {
                s.snapshot.startup = reply.startup.ok_or("worker_output_invalid")?;
                if s.snapshot.startup.error.is_none() && action != "query" {
                    s.record(
                        if action == "enable" {
                            "startup_enabled"
                        } else {
                            "startup_disabled"
                        },
                        "info",
                        None,
                    );
                }
            }
            Err(e) => {
                s.snapshot.startup = protocol::Startup {
                    state: "error".into(),
                    error: Some(e.into()),
                    ..Default::default()
                };
            }
        }
        s.snapshot.clone()
    };
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
async fn set_startup(
    enabled: bool,
    state: tauri::State<'_, Shared>,
    app: tauri::AppHandle,
) -> Result<Snapshot, String> {
    startup_operation(
        state.inner().clone(),
        app,
        if enabled { "enable" } else { "disable" },
    )
    .await
}
#[tauri::command]
async fn choose_directory(app: tauri::AppHandle) -> Result<Option<String>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        app.dialog()
            .file()
            .blocking_pick_folder()
            .map(|p| {
                p.into_path()
                    .map(|p| p.to_string_lossy().into_owned())
                    .map_err(|_| "dialog_failed".into())
            })
            .transpose()
    })
    .await
    .map_err(|_| "dialog_failed")?
}
#[tauri::command]
async fn export_diagnostics(
    state: tauri::State<'_, Shared>,
    app: tauri::AppHandle,
) -> Result<bool, String> {
    export(state.inner().clone(), app).await
}
async fn export(state: Shared, app: tauri::AppHandle) -> Result<bool, String> {
    let mut diagnostics = lock(&state)?.diagnostics();
    if let Some(host) = app.try_state::<crate::host::Shared>() {
        diagnostics["host"] = crate::host::diagnostics(&host);
    }
    tauri::async_runtime::spawn_blocking(move || {
        let selected = app
            .dialog()
            .file()
            .set_file_name("NetDriveGuard-diagnostic.json")
            .add_filter("JSON", &["json"])
            .blocking_save_file();
        let Some(selected) = selected else {
            return Ok(false);
        };
        let path = selected.into_path().map_err(|_| "export_failed")?;
        net_drive_guard_app::storage::atomic(&path, &diagnostics).map_err(|_| "export_failed")?;
        Ok(true)
    })
    .await
    .map_err(|_| "export_failed")?
}
#[tauri::command]
fn open_folder(target: String, state: tauri::State<Shared>) -> Result<(), String> {
    let s = lock(&state)?;
    let path = match target.as_str() {
        "data" => s.snapshot.data_directory.clone(),
        "mapping" if s.snapshot.configured => format!("{}\\", s.snapshot.settings.drive),
        _ => return Err("invalid_target".into()),
    };
    std::process::Command::new("explorer.exe")
        .arg(path)
        .spawn()
        .map_err(|_| "open_failed")?;
    Ok(())
}
#[tauri::command]
fn hide_window(app: tauri::AppHandle) -> Result<(), String> {
    app.get_webview_window("main")
        .ok_or_else(|| "window_unavailable".to_string())?
        .hide()
        .map_err(|_| "window_unavailable".to_string())
}

fn fit_window(app: &tauri::AppHandle) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let Ok(Some(monitor)) = window.current_monitor() else {
        return;
    };
    let area = monitor.work_area();
    let scale = monitor.scale_factor();
    // Reserve space for the native frame and taskbar at the current DPI.
    let width = 760.0_f64.min((f64::from(area.size.width) / scale - 32.0).max(1.0));
    let height = 620.0_f64.min((f64::from(area.size.height) / scale - 64.0).max(1.0));
    let _ = window.set_min_size(Some(tauri::LogicalSize::new(
        560.0_f64.min(width),
        420.0_f64.min(height),
    )));
    let _ = window.set_size(tauri::LogicalSize::new(width, height));
    if let Ok(size) = window.outer_size() {
        let _ = window.set_position(tauri::PhysicalPosition::new(
            area.position.x
                + (i64::from(area.size.width).saturating_sub(i64::from(size.width)) / 2) as i32,
            area.position.y
                + (i64::from(area.size.height).saturating_sub(i64::from(size.height)) / 2) as i32,
        ));
    }
}

#[tauri::command]
fn quit_app(state: tauri::State<Shared>, app: tauri::AppHandle) {
    state.stop.store(true, Ordering::Release);
    state.cancel.store(true, Ordering::Release);
    if let Some(host) = app.try_state::<crate::host::Shared>() {
        crate::host::stop(&host);
    }
    app.exit(0);
}
fn show(app: &tauri::AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.show();
        let _ = w.unminimize();
        let _ = w.set_focus();
    }
}
fn icon(state: &str) -> tauri::image::Image<'static> {
    let accent = match state {
        "healthy" => [71, 213, 170, 255],
        "paused" | "not_configured" => [150, 170, 182, 255],
        "checking" | "repairing" => [241, 190, 88, 255],
        _ => [240, 125, 99, 255],
    };
    let mut pixels = vec![0; 32 * 32 * 4];
    for y in 0..32 {
        for x in 0..32 {
            let i = (y * 32 + x) * 4;
            let color = if (7..25).contains(&x) && (9..23).contains(&y) {
                accent
            } else {
                [21, 37, 49, 255]
            };
            pixels[i..i + 4].copy_from_slice(&color);
        }
    }
    tauri::image::Image::new_owned(pixels, 32, 32)
}
fn state_text<'a>(state: &'a str, lang: &str) -> &'a str {
    if lang == "en" {
        return match state {
            "healthy" => "Connection healthy",
            "server_offline" => "Server offline",
            "paused" => "Monitoring paused",
            "repairing" => "Restoring mapping",
            "authentication_failed" => "Authentication failed",
            "share_unavailable" => "Share unavailable",
            "drive_conflict" => "Drive conflict",
            "mapping_failed" => "Mapping operation failed",
            "not_configured" => "Not configured",
            _ => "Checking connection",
        };
    }
    match state {
        "healthy" => "连接正常",
        "server_offline" => "服务器离线",
        "paused" => "监控已暂停",
        "repairing" => "正在恢复映射",
        "authentication_failed" => "身份验证失败",
        "share_unavailable" => "共享不可访问",
        "drive_conflict" => "盘符冲突",
        "mapping_failed" => "映射操作失败",
        "not_configured" => "尚未配置",
        "internal_error" => "检查失败",
        _ => "正在检查连接",
    }
}
fn supervise(app: tauri::AppHandle, state: Shared) {
    let signals = net_drive_guard_windows::signals::Signals::new();
    if signals.is_err() {
        if let Ok(mut s) = lock(&state) {
            s.record("signals_unavailable", "warning", None);
        }
    }
    while !state.stop.load(Ordering::Acquire) {
        let request = {
            let Ok(mut s) = lock(&state) else { break };
            if signals.as_ref().is_ok_and(|v| v.take() != 0) {
                if !s.snapshot.paused {
                    s.requested = Some(Trigger::NetworkChanged);
                }
                if let Some(host) = app.try_state::<crate::host::Shared>() {
                    crate::host::request_refresh(&host);
                }
            }
            let request = s.prepare(state.epoch.elapsed());
            if request.is_some() {
                state.cancel.store(false, Ordering::Release);
            }
            request
        };
        if let Some(request) = request {
            publish(&app, &state);
            let settings = match lock(&state) {
                Ok(s) => s.snapshot.settings.clone(),
                Err(_) => break,
            };
            let result = protocol::probe(&settings, request.checks, &state.cancel);
            let action = match lock(&state) {
                Ok(mut s) => s.complete(&request, result, state.epoch.elapsed()),
                Err(_) => break,
            };
            publish(&app, &state);
            if action != Action::None {
                let result = protocol::run(
                    &protocol::Request {
                        settings,
                        job: Job::Remediate {
                            remove: action == Action::RemoveMapping,
                        },
                    },
                    Duration::from_secs(30),
                    &state.cancel,
                )
                .and_then(|r| r.operation.ok_or("worker_output_invalid".into()));
                if let Ok(mut s) = lock(&state) {
                    s.complete_operation(result, state.epoch.elapsed());
                }
                publish(&app, &state);
            }
        }
        thread::sleep(Duration::from_millis(200));
    }
}
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| show(app)))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_notification::init())
        .setup(|app| {
            let root = std::env::var_os("LOCALAPPDATA")
                .map(PathBuf::from)
                .ok_or("LOCALAPPDATA missing")?
                .join("NetDriveGuard");
            #[cfg(debug_assertions)]
            let root = std::env::var_os("NDG_SMOKE_DATA_DIR")
                .map(PathBuf::from)
                .unwrap_or(root);
            let mut session = Session::open(root);
            if session.snapshot.configured {
                if let Ok(config) = session.snapshot.settings.validate() {
                    match net_drive_guard_windows::credentials::read(&config) {
                        Ok(value) => session.snapshot.credential_saved = value.is_some(),
                        Err(_) => session.snapshot.error = Some("credential_read_failed".into()),
                    }
                }
            }
            let background = std::env::args().any(|a| a == "--background");
            let state = Arc::new(AppState {
                session: Mutex::new(session),
                cancel: AtomicBool::new(false),
                stop: AtomicBool::new(false),
                epoch: Instant::now(),
            });
            app.manage(state.clone());
            let host_directory = PathBuf::from(
                &lock(&state)
                    .map_err(std::io::Error::other)?
                    .snapshot
                    .data_directory,
            );
            let host = crate::host::start(app.handle(), host_directory);
            let background = background
                && (lock(&state)
                    .map_err(std::io::Error::other)?
                    .snapshot
                    .configured
                    || crate::host::is_configured(&host));
            app.manage(host);
            let hint = tauri::WebviewWindowBuilder::new(
                app,
                "control-hint",
                tauri::WebviewUrl::App("index.html?hint=1".into()),
            )
            .title("NetDriveGuard")
            .inner_size(480.0, 44.0)
            .decorations(false)
            .resizable(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .focused(false)
            .visible(false)
            .build()?;
            hint.set_ignore_cursor_events(true)?;
            let open = MenuItem::with_id(app, "open", "打开 / Open", true, None::<&str>)?;
            let check =
                MenuItem::with_id(app, "check", "立即检查 / Check now", true, None::<&str>)?;
            let pause = MenuItem::with_id(
                app,
                "pause",
                "暂停 / 恢复 · Pause / Resume",
                true,
                None::<&str>,
            )?;
            let settings =
                MenuItem::with_id(app, "settings", "设置 / Settings", true, None::<&str>)?;
            let diagnostics = MenuItem::with_id(
                app,
                "diagnostics",
                "脱敏诊断 / Diagnostics",
                true,
                None::<&str>,
            )?;
            let quit = MenuItem::with_id(app, "quit", "退出 / Quit", true, None::<&str>)?;
            let menu = Menu::with_items(
                app,
                &[&open, &check, &pause, &settings, &diagnostics, &quit],
            )?;
            TrayIconBuilder::with_id("main-tray")
                .icon(icon("not_configured"))
                .tooltip("NetDriveGuard")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| {
                    let state = app.state::<Shared>();
                    match event.id.as_ref() {
                        "open" => show(app),
                        "quit" => quit_app(state, app.clone()),
                        "check" => {
                            let _ = check_now(state, app.clone());
                        }
                        "pause" => {
                            let paused = lock(&state).map(|s| !s.snapshot.paused).unwrap_or(true);
                            let _ = set_paused(paused, state, app.clone());
                        }
                        "settings" => {
                            show(app);
                            let _ = app.emit("navigate", "settings");
                        }
                        "diagnostics" => {
                            let state = state.inner().clone();
                            let app = app.clone();
                            tauri::async_runtime::spawn(async move {
                                let _ = export(state, app).await;
                            });
                        }
                        _ => {}
                    }
                })
                .on_tray_icon_event(|tray, event| {
                    if matches!(
                        event,
                        TrayIconEvent::Click {
                            button: MouseButton::Left,
                            button_state: MouseButtonState::Up,
                            ..
                        }
                    ) {
                        show(tray.app_handle());
                    }
                })
                .build(app)?;
            fit_window(app.handle());
            if background {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.hide();
                }
            }
            let handle = app.handle().clone();
            let initial = state.clone();
            tauri::async_runtime::spawn(async move {
                let _ = startup_operation(initial.clone(), handle.clone(), "query").await;
                thread::spawn(move || supervise(handle, initial));
            });
            Ok(())
        })
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            crate::host::host_snapshot,
            crate::host::host_monitors,
            crate::host::host_save,
            crate::host::host_check,
            crate::host::host_pause,
            crate::host::host_connect,
            get_snapshot,
            get_events,
            save_settings,
            check_now,
            set_paused,
            set_credential,
            set_startup,
            save_preferences,
            relocate_data,
            choose_directory,
            export_diagnostics,
            open_folder,
            hide_window,
            quit_app
        ])
        .run(tauri::generate_context!())
        .expect("failed to start NetDriveGuard");
}

pub(crate) fn notification_preferences(app: &tauri::AppHandle) -> (bool, String) {
    app.try_state::<Shared>()
        .and_then(|state| {
            lock(&state).ok().map(|s| {
                (
                    s.snapshot.settings.notifications,
                    s.snapshot.settings.language.clone(),
                )
            })
        })
        .unwrap_or((true, "zh".into()))
}
