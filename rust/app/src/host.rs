//! 开发主机配置与状态独立于网络盘，远程窗口不属于可回收检查进程。
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct HostSettings {
    pub name: String,
    pub address: String,
    pub ssh_port: u16,
    pub rdp_port: u16,
    pub smb_port: u16,
    pub interval_secs: u64,
    pub arrangement: String,
    pub monitor: String,
    pub display: String,
    pub reposition_on_activate: bool,
    pub switch_hint: bool,
    pub connect_on_start: bool,
    pub connect_on_recovery: bool,
}
impl Default for HostSettings {
    fn default() -> Self {
        Self {
            name: "Linux 开发机".into(),
            address: String::new(),
            ssh_port: 22,
            rdp_port: 3389,
            smb_port: 445,
            interval_secs: 10,
            arrangement: "current".into(),
            monitor: "primary".into(),
            display: "maximized".into(),
            reposition_on_activate: false,
            switch_hint: true,
            connect_on_start: false,
            connect_on_recovery: false,
        }
    }
}
impl HostSettings {
    pub fn validate(&self) -> Result<(), String> {
        let host = self.address.trim();
        if host.is_empty()
            || host.len() > 253
            || host != self.address
            || !host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | ':' | '[' | ']'))
            || host.starts_with('-')
            || host.parse::<std::net::IpAddr>().is_err() && host.contains(':')
        {
            return Err("host_address_invalid".into());
        }
        if self.name.trim().is_empty()
            || self.name.chars().count() > 40
            || self.name.chars().any(char::is_control)
            || self.rdp_port == 0
            || !(2..=1800).contains(&self.interval_secs)
            || !matches!(self.arrangement.as_str(), "current" | "dedicated")
            || !matches!(self.display.as_str(), "window" | "maximized" | "fullscreen")
            || self.monitor.is_empty()
            || self.monitor.len() > 1024
            || self.monitor.chars().any(char::is_control)
        {
            return Err("host_settings_invalid".into());
        }
        Ok(())
    }
    pub fn endpoint(&self) -> String {
        if self.address.contains(':') {
            format!("[{}]:{}", self.address, self.rdp_port)
        } else {
            format!("{}:{}", self.address, self.rdp_port)
        }
    }
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Service {
    pub port: u16,
    pub reachable: Option<bool>,
    pub failures: u8,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostEvent {
    pub at_ms: u64,
    pub code: String,
    #[serde(default)]
    pub native_code: Option<u32>,
}
impl HostEvent {
    pub fn as_event(&self) -> crate::Event {
        let source = if self.code.starts_with("rdp_")
            || self.code.starts_with("monitor_")
            || self.code.starts_with("virtual_desktop_")
            || self.code == "display_changed"
        {
            "remote"
        } else {
            "host"
        };
        let severity = match self.code.as_str() {
            "rdp_connected"
            | "rdp_reconnecting"
            | "host_recovered"
            | "rdp_window_open"
            | "rdp_window_closed"
            | "rdp_user_action_required"
            | "display_changed" => "info",
            "rdp_disconnected" | "host_unreachable" | "monitor_fallback" | "rdp_unreachable" => {
                "warning"
            }
            _ => "error",
        };
        crate::Event {
            id: self.at_ms,
            at_ms: self.at_ms,
            code: self.code.clone(),
            severity: severity.into(),
            source: source.into(),
            state: String::new(),
            native_code: self.native_code,
        }
    }
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostSnapshot {
    pub settings: HostSettings,
    pub configured: bool,
    pub paused: bool,
    pub busy: bool,
    pub connecting: bool,
    pub services: Vec<Service>,
    pub last_check_at: Option<u64>,
    pub connection: String,
    pub connection_code: Option<u32>,
    pub error: Option<String>,
    pub probe_error: Option<String>,
    pub events: Vec<HostEvent>,
}
pub struct HostSession {
    pub snapshot: HostSnapshot,
    pub directory: PathBuf,
    pub revision: u64,
    pub requested: bool,
    pub next_check: u64,
    pub startup_pending: bool,
}
impl HostSession {
    pub fn open(directory: PathBuf) -> Self {
        let path = directory.join("host.json");
        let loaded = if path.exists() {
            std::fs::read(&path)
                .map_err(|_| "host_config_invalid".to_owned())
                .and_then(|v| {
                    serde_json::from_slice::<HostSettings>(&v)
                        .map_err(|_| "host_config_invalid".into())
                })
                .and_then(|v| {
                    v.validate()?;
                    Ok(v)
                })
        } else {
            Ok(HostSettings::default())
        };
        let error = loaded.as_ref().err().cloned();
        let settings = loaded.unwrap_or_default();
        let configured = !settings.address.is_empty();
        let events_path = directory.join("host-events.json");
        let events = std::fs::metadata(&events_path)
            .ok()
            .filter(|m| m.len() < 256 * 1024)
            .and_then(|_| std::fs::read(events_path).ok())
            .and_then(|v| serde_json::from_slice::<Vec<HostEvent>>(&v).ok())
            .unwrap_or_default()
            .into_iter()
            .rev()
            .take(100)
            .collect::<Vec<_>>()
            .into_iter()
            .rev()
            .collect();
        Self {
            snapshot: HostSnapshot {
                settings,
                configured,
                paused: false,
                busy: false,
                connecting: false,
                services: vec![],
                last_check_at: None,
                connection: "closed".into(),
                connection_code: None,
                error,
                probe_error: None,
                events,
            },
            directory,
            revision: 0,
            requested: true,
            next_check: 0,
            startup_pending: true,
        }
    }
    pub fn save(&mut self, settings: HostSettings) -> Result<(), String> {
        settings.validate()?;
        if self.snapshot.connecting {
            return Err("host_operation_busy".into());
        }
        crate::storage::atomic(&self.directory.join("host.json"), &settings)?;
        let target_changed = self.snapshot.settings.address != settings.address
            || self.snapshot.settings.smb_port != settings.smb_port
            || self.snapshot.settings.ssh_port != settings.ssh_port
            || self.snapshot.settings.rdp_port != settings.rdp_port;
        self.snapshot.settings = settings;
        self.snapshot.configured = true;
        self.snapshot.error = None;
        if target_changed {
            self.snapshot.services.clear();
            self.snapshot.last_check_at = None;
            self.snapshot.probe_error = None;
        }
        self.revision += 1;
        self.requested = true;
        Ok(())
    }
    pub fn pause(&mut self, paused: bool) {
        self.snapshot.paused = paused;
        self.revision += 1;
        self.requested = !paused;
    }
    pub fn event(&mut self, code: &str) {
        self.event_code(code, None);
    }
    pub fn event_code(&mut self, code: &str, native_code: Option<u32>) {
        if self
            .snapshot
            .events
            .last()
            .is_some_and(|e| e.code == code && e.native_code == native_code)
        {
            return;
        }
        self.snapshot.events.push(HostEvent {
            at_ms: crate::wall_time(),
            code: code.into(),
            native_code,
        });
        if self.snapshot.events.len() > 100 {
            self.snapshot.events.remove(0);
        }
        let storage = crate::storage::Storage {
            directory: self.directory.clone(),
            root: self.directory.clone(),
            log_limit: crate::storage::LOG_LIMIT,
        };
        let logged = storage.append(
            &self
                .snapshot
                .events
                .last()
                .expect("event was inserted")
                .as_event(),
        );
        if crate::storage::atomic(
            &self.directory.join("host-events.json"),
            &self.snapshot.events,
        )
        .is_err()
            || logged.is_err()
        {
            self.snapshot.error = Some("host_log_failed".into());
        }
    }
    pub fn complete(&mut self, revision: u64, result: Result<Vec<bool>, String>) -> bool {
        self.snapshot.busy = false;
        if revision != self.revision || self.snapshot.paused {
            return false;
        }
        self.next_check = crate::wall_time() + self.snapshot.settings.interval_secs * 1000;
        self.snapshot.last_check_at = Some(crate::wall_time());
        let values = match result {
            Ok(v) if v.len() == 3 => v,
            Ok(_) => {
                self.snapshot.probe_error = Some("host_probe_failed".into());
                return false;
            }
            Err(e) => {
                self.snapshot.probe_error = Some(e);
                return false;
            }
        };
        let rdp_was_down = self
            .snapshot
            .services
            .get(2)
            .is_some_and(|s| s.failures >= 3);
        let was_down = !self.snapshot.services.is_empty()
            && self
                .snapshot
                .services
                .iter()
                .filter(|s| s.port != 0)
                .all(|s| s.failures >= 3);
        let ports = [
            self.snapshot.settings.smb_port,
            self.snapshot.settings.ssh_port,
            self.snapshot.settings.rdp_port,
        ];
        self.snapshot.services = ports
            .into_iter()
            .enumerate()
            .map(|(i, port)| Service {
                port,
                reachable: if port == 0 { None } else { Some(values[i]) },
                failures: if port == 0 || values[i] {
                    0
                } else {
                    self.snapshot
                        .services
                        .get(i)
                        .map_or(1, |s| s.failures.saturating_add(1))
                },
            })
            .collect();
        self.snapshot.probe_error = None;
        let now_down = self
            .snapshot
            .services
            .iter()
            .filter(|s| s.port != 0)
            .all(|s| s.failures >= 3);
        if now_down && !was_down {
            self.event("host_unreachable");
        }
        if was_down && values.iter().any(|v| *v) {
            self.event("host_recovered");
            return true;
        }
        rdp_was_down && values[2]
    }
}
