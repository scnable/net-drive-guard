use net_drive_guard_app::{
    host::{HostSession, HostSettings, HostSnapshot},
    protocol::{self, Job, Request},
    Settings,
};
use net_drive_guard_windows::remote::{self, Monitor, RemoteWindow};
use serde::{Deserialize, Serialize};
use std::{
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
use tauri::{Emitter, Manager};
use tauri_plugin_notification::NotificationExt;

#[derive(Default, Serialize, Deserialize)]
struct Connection {
    window: Option<RemoteWindow>,
    desktop: Option<String>,
    #[serde(default)]
    pending: bool,
}
pub struct Controller {
    session: Mutex<HostSession>,
    connection: Mutex<Connection>,
    stop: AtomicBool,
    refresh_requested: AtomicBool,
}
pub type Shared = Arc<Controller>;
fn publish(app: &tauri::AppHandle, state: &Controller) {
    if let Ok(s) = state.session.lock() {
        let _ = app.emit("host-changed", s.snapshot.clone());
    }
}
#[tauri::command]
pub fn host_snapshot(state: tauri::State<Shared>) -> Result<HostSnapshot, String> {
    Ok(state
        .session
        .lock()
        .map_err(|_| "state_unavailable")?
        .snapshot
        .clone())
}
#[tauri::command]
pub fn host_monitors() -> Vec<Monitor> {
    remote::monitors()
}
#[tauri::command]
pub fn host_save(
    settings: HostSettings,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<HostSnapshot, String> {
    let snapshot = {
        let mut s = state.session.lock().map_err(|_| "state_unavailable")?;
        s.save(settings)?;
        s.snapshot.clone()
    };
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
pub fn host_check(state: tauri::State<Shared>) -> Result<(), String> {
    state
        .session
        .lock()
        .map_err(|_| "state_unavailable")?
        .requested = true;
    Ok(())
}
#[tauri::command]
pub fn host_pause(
    paused: bool,
    state: tauri::State<Shared>,
    app: tauri::AppHandle,
) -> Result<HostSnapshot, String> {
    let snapshot = {
        let mut s = state.session.lock().map_err(|_| "state_unavailable")?;
        s.pause(paused);
        s.snapshot.clone()
    };
    publish(&app, &state);
    Ok(snapshot)
}
#[tauri::command]
pub async fn host_connect(
    state: tauri::State<'_, Shared>,
    app: tauri::AppHandle,
) -> Result<(), String> {
    let state = state.inner().clone();
    {
        let mut s = state.session.lock().map_err(|_| "state_unavailable")?;
        s.snapshot.settings.validate()?;
        if s.snapshot.connecting {
            return Err("host_operation_busy".into());
        }
        s.snapshot.connecting = true;
        s.snapshot.error = None;
    }
    publish(&app, &state);
    tauri::async_runtime::spawn_blocking(move || connect(&app, &state))
        .await
        .map_err(|_| "rdp_start_failed".to_owned())?
}
fn connect(app: &tauri::AppHandle, state: &Controller) -> Result<(), String> {
    let result: Result<Option<String>, String> = (|| {
        let mut c = state.connection.lock().map_err(|_| "state_unavailable")?;
        let (settings, directory) = {
            let s = state.session.lock().map_err(|_| "state_unavailable")?;
            (s.snapshot.settings.clone(), s.directory.clone())
        };
        let (monitor, fallback) = remote::choose_monitor(&settings.monitor)?;
        let alive = c
            .window
            .as_mut()
            .is_some_and(|w| w.endpoint == settings.endpoint() && w.refresh());
        if !alive {
            // 此检测不持有网络盘锁；检查进程退出不会结束远程客户端。
            let reply = protocol::run(
                &Request {
                    settings: Settings::default(),
                    job: Job::HostProbe {
                        address: settings.address.clone(),
                        ports: vec![0, 0, settings.rdp_port],
                    },
                },
                Duration::from_secs(4),
                &state.stop,
            )?;
            if !reply.host_ports.is_some_and(|p| p.get(2) == Some(&true)) {
                return Err("rdp_unreachable".into());
            }
            std::fs::create_dir_all(&directory).map_err(|_| "config_write_failed")?;
            c.window = Some(remote::launch_native(
                &settings.address,
                settings.rdp_port,
                &settings.name,
                &monitor,
            )?);
            c.pending = true;
            net_drive_guard_app::storage::atomic(&directory.join("remote-window.json"), &*c)?;
        }
        let window = c.window.clone().ok_or("rdp_window_closed")?;
        if alive {
            window.reconnect()?;
        }
        if !window.ready() {
            c.pending = true;
            net_drive_guard_app::storage::atomic(&directory.join("remote-window.json"), &*c)?;
            return Ok(Some("rdp_user_action_required".into()));
        }
        c.pending = false;
        let mut warning = if fallback {
            Some("monitor_fallback".to_owned())
        } else {
            None
        };
        match remote::arrange(&window, settings.arrangement == "dedicated", &mut c.desktop) {
            Ok(id) => {
                if id.is_some() {
                    c.desktop = id;
                }
            }
            Err(e) => warning = Some(e),
        }
        net_drive_guard_app::storage::atomic(&directory.join("remote-window.json"), &*c)?;
        remote::place(&window, &monitor, &settings.display, true)?;
        net_drive_guard_app::storage::atomic(&directory.join("remote-window.json"), &*c)?;
        Ok(warning)
    })();
    let connection_status = state
        .connection
        .lock()
        .ok()
        .and_then(|c| c.window.as_ref().map(|w| w.connection_state()))
        .unwrap_or("closed");
    {
        let mut s = state.session.lock().map_err(|_| "state_unavailable")?;
        s.snapshot.connecting = false;
        match &result {
            Ok(warning) => {
                let pending = warning.as_deref() == Some("rdp_user_action_required");
                s.snapshot.connection = if pending {
                    "waiting"
                } else {
                    connection_status
                }
                .into();
                s.snapshot.error = if pending { None } else { warning.clone() };
                s.event(if pending {
                    "rdp_user_action_required"
                } else if connection_status == "connected" {
                    "rdp_connected"
                } else {
                    "rdp_window_open"
                });
            }
            Err(e) => {
                s.snapshot.error = Some(e.clone());
                s.event(e);
            }
        }
    }
    publish(app, state);
    result.map(|_| ())
}
pub fn is_configured(state: &Controller) -> bool {
    state.session.lock().is_ok_and(|s| s.snapshot.configured)
}
pub fn request_refresh(state: &Controller) {
    state.refresh_requested.store(true, Ordering::Release);
}
pub fn stop(state: &Controller) {
    state.stop.store(true, Ordering::Release);
}
pub fn with_storage_change<T>(
    state: &Controller,
    change: impl FnOnce() -> Result<(Result<T, String>, PathBuf), String>,
) -> Result<T, String> {
    let _connection = state.connection.lock().map_err(|_| "state_unavailable")?;
    let mut session = state.session.lock().map_err(|_| "state_unavailable")?;
    let (result, directory) = change()?;
    session.directory = directory;
    result
}
pub fn start(app: &tauri::AppHandle, directory: PathBuf) -> Shared {
    let connection = std::fs::read(directory.join("remote-window.json"))
        .ok()
        .and_then(|v| serde_json::from_slice(&v).ok())
        .unwrap_or_default();
    let state = Arc::new(Controller {
        session: Mutex::new(HostSession::open(directory)),
        connection: Mutex::new(connection),
        stop: AtomicBool::new(false),
        refresh_requested: AtomicBool::new(false),
    });
    let handle = app.clone();
    let monitor_state = state.clone();
    thread::spawn(move || {
        let state = monitor_state;
        while !state.stop.load(Ordering::Acquire) {
            let request = {
                let mut s = match state.session.lock() {
                    Ok(v) => v,
                    Err(_) => return,
                };
                if state.refresh_requested.swap(false, Ordering::AcqRel) {
                    s.requested = true;
                }
                if s.snapshot.configured
                    && !s.snapshot.paused
                    && !s.snapshot.busy
                    && (s.requested || net_drive_guard_app::wall_time() >= s.next_check)
                {
                    s.requested = false;
                    s.snapshot.busy = true;
                    Some((s.revision, s.snapshot.settings.clone()))
                } else {
                    None
                }
            };
            if let Some((revision, settings)) = request {
                let result = protocol::run(
                    &Request {
                        settings: Settings::default(),
                        job: Job::HostProbe {
                            address: settings.address.clone(),
                            ports: vec![settings.smb_port, settings.ssh_port, settings.rdp_port],
                        },
                    },
                    Duration::from_secs(5),
                    &state.stop,
                )
                .and_then(|r| r.host_ports.ok_or("host_probe_failed".into()));
                let auto = {
                    let mut s = match state.session.lock() {
                        Ok(v) => v,
                        Err(_) => return,
                    };
                    let previous = s.snapshot.events.last().map(|e| e.at_ms);
                    let current = revision == s.revision && !s.snapshot.paused;
                    let recovered = s.complete(revision, result);
                    if current && s.snapshot.events.last().map(|e| e.at_ms) != previous {
                        if let Some(event) = s.snapshot.events.last() {
                            let (enabled, language) =
                                crate::desktop::notification_preferences(&handle);
                            if enabled {
                                let text = match (event.code.as_str(), language.as_str()) {
                                    ("host_recovered", "en") => {
                                        "Development host is responding again"
                                    }
                                    ("host_unreachable", "en") => {
                                        "No configured service is responding"
                                    }
                                    ("host_recovered", _) => "开发主机恢复响应",
                                    ("host_unreachable", _) => "开发主机已配置的服务均无响应",
                                    _ => "",
                                };
                                if !text.is_empty() {
                                    let _ = handle
                                        .notification()
                                        .builder()
                                        .title("NetDriveGuard")
                                        .body(text)
                                        .show();
                                }
                            }
                        }
                    }
                    let on_start =
                        current && s.startup_pending && s.snapshot.settings.connect_on_start;
                    s.startup_pending = false;
                    let auto = (on_start || recovered && s.snapshot.settings.connect_on_recovery)
                        && !s.snapshot.connecting
                        && s.snapshot
                            .services
                            .get(2)
                            .is_some_and(|v| v.reachable == Some(true));
                    if auto {
                        s.snapshot.connecting = true;
                    }
                    auto
                };
                publish(&handle, &state);
                if auto {
                    let _ = connect(&handle, &state);
                }
            }
            thread::sleep(Duration::from_millis(200));
        }
    });
    let handle = app.clone();
    let observe_state = state.clone();
    thread::spawn(move || {
        let mut previous: Option<(bool, String)> = None;
        let mut hide_at = None;
        let mut displays = remote::monitors();
        displays.sort_by(|a, b| a.id.cmp(&b.id));
        let mut candidate = displays.clone();
        let mut display_check = Instant::now();
        let mut display_pending = false;
        let mut display_retry = Instant::now();
        while !observe_state.stop.load(Ordering::Acquire) {
            let settings = match observe_state.session.lock() {
                Ok(v) => v.snapshot.settings.clone(),
                Err(_) => return,
            };
            let (window, arrange_pending) = match observe_state.connection.try_lock() {
                Ok(mut c) => {
                    let alive = c
                        .window
                        .as_mut()
                        .is_some_and(|w| w.endpoint == settings.endpoint() && w.refresh());
                    if !alive {
                        c.pending = false;
                    }
                    (
                        if alive { c.window.clone() } else { None },
                        c.pending && alive,
                    )
                }
                Err(_) => {
                    thread::sleep(Duration::from_millis(100));
                    continue;
                }
            };
            if arrange_pending && window.as_ref().is_some_and(|w| w.ready()) {
                let run = observe_state.session.lock().ok().is_some_and(|mut s| {
                    if s.snapshot.connecting {
                        return false;
                    }
                    s.snapshot.connecting = true;
                    true
                });
                if run {
                    if let Ok(mut c) = observe_state.connection.lock() {
                        c.pending = false;
                    }
                    let _ = connect(&handle, &observe_state);
                    continue;
                }
            }
            let no_window = window.is_none();
            if let Some(window) = window {
                let valid = window.valid();
                let active = valid && remote::foreground() == window.hwnd;
                if display_check.elapsed() >= Duration::from_secs(1) {
                    display_check = Instant::now();
                    let mut current_displays = remote::monitors();
                    current_displays.sort_by(|a, b| a.id.cmp(&b.id));
                    // 连续两次读取一致才处理，避免拔插过程中的临时空列表。
                    if !current_displays.is_empty()
                        && current_displays == candidate
                        && current_displays != displays
                    {
                        displays = current_displays.clone();
                        display_pending = true;
                    }
                    candidate = current_displays;
                }
                // mstsc 切换全屏需要前台键盘权限；后台窗口在再次激活时处理，避免抢走其他应用的输入。
                if display_pending
                    && active
                    && !arrange_pending
                    && display_retry.elapsed() >= Duration::from_secs(2)
                {
                    display_retry = Instant::now();
                    let result =
                        remote::choose_monitor(&settings.monitor).and_then(|(m, fallback)| {
                            remote::place(&window, &m, &settings.display, false)?;
                            Ok(fallback)
                        });
                    if let Ok(mut s) = observe_state.session.lock() {
                        match result {
                            Ok(fallback) => {
                                display_pending = false;
                                s.snapshot.error = fallback.then(|| "monitor_fallback".into());
                                s.event(if fallback {
                                    "monitor_fallback"
                                } else {
                                    "display_changed"
                                });
                            }
                            Err(error) => {
                                s.snapshot.error = Some(error.clone());
                                s.event(&error);
                            }
                        }
                    }
                    publish(&handle, &observe_state);
                }
                let mut monitor = remote::foreground_monitor();
                let mut current = (
                    active,
                    monitor.as_ref().map(|m| m.id.clone()).unwrap_or_default(),
                );
                if previous.as_ref() != Some(&current) {
                    if active
                        && previous.as_ref().is_some_and(|p| !p.0)
                        && settings.reposition_on_activate
                    {
                        if let Ok((m, _)) = remote::choose_monitor(&settings.monitor) {
                            if let Err(error) = remote::place(&window, &m, &settings.display, false)
                            {
                                if let Ok(mut s) = observe_state.session.lock() {
                                    s.snapshot.error = Some(error.clone());
                                    s.event(&error);
                                }
                                publish(&handle, &observe_state);
                            }
                            monitor = remote::foreground_monitor();
                            current.1 = monitor.as_ref().map(|m| m.id.clone()).unwrap_or_default();
                        }
                    }
                    let changed = previous
                        .as_ref()
                        .map_or(active, |p| p.0 != active || active && p.1 != current.1);
                    if changed && settings.switch_hint {
                        if let Some(m) = monitor {
                            let (_, language) = crate::desktop::notification_preferences(&handle);
                            let text = match (active, language.as_str()) {
                                (true, "en") => {
                                    format!("Current window: {} remote connection", settings.name)
                                }
                                (false, "en") => "Current control: local Windows".into(),
                                (true, _) => format!("当前窗口：{}远程连接", settings.name),
                                (false, _) => "当前控制：本机 Windows".into(),
                            };
                            show_hint(&handle, &m, &text);
                            hide_at = Some(Instant::now() + Duration::from_millis(1500));
                        }
                    }
                    previous = Some(current);
                }
                let mut changed = false;
                if let Ok(mut s) = observe_state.session.lock() {
                    let status = if valid && window.native() {
                        window.connection_state()
                    } else if arrange_pending {
                        "waiting"
                    } else if valid {
                        window.connection_state()
                    } else {
                        "closed"
                    };
                    let connection_code = window.connection_code();
                    if s.snapshot.connection != status
                        || s.snapshot.connection_code != connection_code
                    {
                        s.snapshot.connection = status.into();
                        s.snapshot.connection_code = connection_code;
                        if status == "connected"
                            && s.snapshot
                                .error
                                .as_ref()
                                .is_some_and(|e| e.starts_with("rdp_"))
                        {
                            s.snapshot.error = None;
                        }
                        changed = true;
                        s.event_code(
                            match status {
                                "connected" => "rdp_connected",
                                "disconnected" => "rdp_disconnected",
                                "reconnecting" => "rdp_reconnecting",
                                "connection_failed" => "rdp_connection_failed",
                                "closed" => "rdp_window_closed",
                                _ => "rdp_window_open",
                            },
                            connection_code,
                        );
                    }
                }
                if changed {
                    publish(&handle, &observe_state);
                }
            }
            if no_window {
                previous = None;
                display_pending = false;
                if let Ok(mut s) = observe_state.session.lock() {
                    if s.snapshot.connection != "closed" {
                        s.snapshot.connection = "closed".into();
                        s.snapshot.connection_code = None;
                        s.event("rdp_window_closed");
                        drop(s);
                        publish(&handle, &observe_state);
                    }
                }
            }
            if !settings.switch_hint || hide_at.is_some_and(|v| Instant::now() >= v) {
                if let Some(w) = handle.get_webview_window("control-hint") {
                    if let Ok(h) = w.hwnd() {
                        remote::hide_hint(h.0 as usize);
                    }
                }
                hide_at = None;
            }
            thread::sleep(Duration::from_millis(100));
        }
    });
    state
}
fn show_hint(app: &tauri::AppHandle, m: &Monitor, text: &str) {
    let Some(window) = app.get_webview_window("control-hint") else {
        return;
    };
    let _ = window.set_size(tauri::PhysicalSize::new(480u32, 48u32));
    let _ = window.set_position(tauri::PhysicalPosition::new(
        m.x + (m.width - 480) / 2,
        m.y + 68,
    ));
    let _ = window.emit("control-hint", text);
    if let Ok(h) = window.hwnd() {
        remote::show_hint(h.0 as usize);
    }
}

pub fn diagnostics(state: &Controller) -> serde_json::Value {
    match state.session.lock() {
        Ok(s) => {
            serde_json::json!({"configured":s.snapshot.configured,"paused":s.snapshot.paused,"connection":s.snapshot.connection,"connectionCode":s.snapshot.connection_code,"services":s.snapshot.services,"events":s.snapshot.events,"error":s.snapshot.error})
        }
        Err(_) => serde_json::Value::Null,
    }
}
