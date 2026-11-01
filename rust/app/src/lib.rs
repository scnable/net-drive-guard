//! Application state is owned by Rust; WebView reloads never restart monitoring.
pub mod host;
pub mod protocol;
pub mod settings;
pub mod storage;
use net_drive_guard_core::{
    monitor::{
        Action, Decision, Event as CoreEvent, Failure, NotificationGate, Observation, State,
    },
    runtime::{Completion, Monitor, Request},
    schedule::{Checks, Intervals, Trigger},
};
use net_drive_guard_windows::probe::ProbeData;
use serde::{Deserialize, Serialize};
pub use settings::Settings;
use std::{
    collections::VecDeque,
    path::{Path, PathBuf},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use storage::Storage;

#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct Event {
    pub id: u64,
    pub at_ms: u64,
    pub code: String,
    pub severity: String,
    pub source: String,
    pub state: String,
    pub native_code: Option<u32>,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub settings: Settings,
    pub configured: bool,
    pub busy: bool,
    pub paused: bool,
    pub state: String,
    pub result: Option<ProbeData>,
    pub events: VecDeque<Event>,
    pub error: Option<String>,
    pub storage_error: Option<String>,
    pub data_directory: String,
    pub startup: protocol::Startup,
    pub credential_saved: bool,
    pub last_check_at: Option<u64>,
    pub last_change_at: Option<u64>,
    pub next_check_at: Option<u64>,
    pub last_tcp_at: Option<u64>,
    pub last_share_at: Option<u64>,
    pub consecutive_failures: u8,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Notice {
    pub code: String,
    pub state: String,
    pub language: String,
}
pub struct Session {
    pub snapshot: Snapshot,
    pub requested: Option<Trigger>,
    pub notice: Option<Notice>,
    storage: Storage,
    monitor: Monitor,
    gate: NotificationGate,
    sequence: u64,
    last_event: String,
    verifying_mapping: bool,
}
pub fn wall_time() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
pub fn state_name(state: State) -> &'static str {
    match state {
        State::NotConfigured => "not_configured",
        State::Checking => "checking",
        State::Healthy => "healthy",
        State::ServerOffline => "server_offline",
        State::MappingMissing => "mapping_missing",
        State::Repairing => "repairing",
        State::AuthenticationFailed => "authentication_failed",
        State::ShareUnavailable => "share_unavailable",
        State::DriveConflict => "drive_conflict",
        State::Paused => "paused",
        State::InternalError => "internal_error",
        State::MappingFailed => "mapping_failed",
    }
}
pub fn failure_name(failure: Failure) -> &'static str {
    match failure {
        Failure::Authentication => "authentication",
        Failure::CredentialConflict => "credential_conflict",
        Failure::ShareUnavailable => "share_unavailable",
        Failure::DriveConflict => "drive_conflict",
        Failure::InUse => "in_use",
        Failure::Internal => "internal",
        Failure::Timeout => "timeout",
        Failure::Mapping => "mapping",
    }
}
fn failure(value: &str) -> Failure {
    match value {
        "authentication" => Failure::Authentication,
        "credential_conflict" => Failure::CredentialConflict,
        "share_unavailable" => Failure::ShareUnavailable,
        "drive_conflict" => Failure::DriveConflict,
        "in_use" => Failure::InUse,
        "timeout" => Failure::Timeout,
        "mapping" => Failure::Mapping,
        _ => Failure::Internal,
    }
}
impl Session {
    pub fn open(root: PathBuf) -> Self {
        let (storage, pointer_error) = Storage::open(root);
        let loaded = if let Some(error) = pointer_error {
            Err(error)
        } else {
            storage.load()
        };
        let (mut settings, configured, mut error) = match loaded {
            Ok(Some(s)) => (s, true, None),
            Ok(None) => (Settings::default(), false, None),
            Err(e) => (Settings::default(), false, Some(e)),
        };
        let preferences_path = storage.directory.join("preferences.json");
        if error.is_none() && preferences_path.exists() {
            let loaded = std::fs::metadata(&preferences_path)
                .ok()
                .filter(|v| v.len() <= 4096)
                .and_then(|_| std::fs::read(&preferences_path).ok())
                .and_then(|bytes| serde_json::from_slice::<settings::Preferences>(&bytes).ok())
                .filter(|v| v.validate().is_ok());
            if let Some(v) = loaded {
                settings.language = v.language;
                settings.notifications = v.notifications;
            } else {
                error = Some("config_invalid".into());
            }
        }
        let events: VecDeque<_> = storage
            .recent()
            .into_iter()
            .filter(|e| e.source.is_empty() || e.source == "mapping")
            .collect();
        let sequence = events.iter().map(|e| e.id).max().unwrap_or(0);
        let config = if configured {
            settings.validate().ok()
        } else {
            None
        };
        let snapshot = Snapshot {
            settings,
            configured,
            busy: false,
            paused: false,
            state: "not_configured".into(),
            result: None,
            events,
            error,
            storage_error: None,
            data_directory: storage.directory.to_string_lossy().into(),
            startup: protocol::Startup {
                state: "unknown".into(),
                ..Default::default()
            },
            credential_saved: false,
            last_check_at: None,
            last_change_at: None,
            next_check_at: None,
            last_tcp_at: None,
            last_share_at: None,
            consecutive_failures: 0,
        };
        Self {
            snapshot,
            storage,
            monitor: Monitor::new(config, Duration::ZERO),
            gate: NotificationGate::new(Duration::from_secs(60)),
            sequence,
            last_event: String::new(),
            verifying_mapping: false,
            requested: if configured {
                Some(Trigger::Startup)
            } else {
                None
            },
            notice: None,
        }
    }
    pub fn record(&mut self, code: &str, severity: &str, native_code: Option<u32>) {
        self.sequence = self.sequence.saturating_add(1);
        let event = Event {
            id: self.sequence,
            at_ms: wall_time(),
            code: code.into(),
            severity: severity.into(),
            source: "mapping".into(),
            state: self.snapshot.state.clone(),
            native_code,
        };
        if let Err(e) = self.storage.append(&event) {
            self.snapshot.storage_error = Some(e);
        }
        self.snapshot.events.push_front(event);
        self.snapshot.events.truncate(100);
    }
    pub fn events(&self) -> VecDeque<Event> {
        self.storage.recent()
    }
    pub fn relocate(&mut self, destination: &Path) -> Result<Snapshot, String> {
        if self.snapshot.busy {
            return Err("busy".into());
        }
        let drive = self
            .snapshot
            .configured
            .then_some(self.snapshot.settings.drive.as_str());
        self.storage.relocate(destination, drive)?;
        self.snapshot.data_directory = self.storage.directory.to_string_lossy().into();
        self.snapshot.storage_error = None;
        Ok(self.snapshot.clone())
    }
    pub fn save_preferences(
        &mut self,
        preferences: settings::Preferences,
    ) -> Result<Snapshot, String> {
        preferences.validate()?;
        storage::atomic(
            &self.storage.directory.join("preferences.json"),
            &preferences,
        )?;
        self.snapshot.settings.language = preferences.language;
        self.snapshot.settings.notifications = preferences.notifications;
        Ok(self.snapshot.clone())
    }
    pub fn save(
        &mut self,
        settings: Settings,
        directory: Option<&Path>,
        now: Duration,
    ) -> Result<Snapshot, String> {
        if self.snapshot.busy {
            return Err("busy".into());
        }
        let settings = settings.canonical()?;
        let config = settings.validate()?;
        self.storage.save(&settings, directory)?;
        self.snapshot.data_directory = self.storage.directory.to_string_lossy().into();
        storage::atomic(
            &self.storage.directory.join("preferences.json"),
            &settings::Preferences::from_settings(&settings),
        )?;
        self.monitor.configure(config, now).map_err(|_| "busy")?;
        self.snapshot.settings = settings;
        self.snapshot.configured = true;
        self.snapshot.error = None;
        self.snapshot.storage_error = None;
        self.snapshot.data_directory = self.storage.directory.to_string_lossy().into();
        self.snapshot.result = None;
        self.snapshot.last_tcp_at = None;
        self.snapshot.last_share_at = None;
        self.gate = NotificationGate::new(Duration::from_secs(60));
        self.last_event.clear();
        self.verifying_mapping = false;
        self.sync(now);
        if !self.snapshot.paused {
            self.requested = Some(Trigger::ConfigurationSaved);
        }
        self.record("settings_saved", "info", None);
        Ok(self.snapshot.clone())
    }
    pub fn pause(&mut self, now: Duration) {
        self.verifying_mapping = false;
        self.monitor.pause(now);
        self.snapshot.paused = true;
        self.requested = None;
        self.sync(now);
        self.record("monitor_paused", "info", None);
    }
    pub fn resume(&mut self, now: Duration) {
        self.monitor.resume(now);
        self.snapshot.paused = false;
        self.requested = Some(Trigger::Resume);
        self.snapshot.result = None;
        self.sync(now);
        self.record("monitor_resumed", "info", None);
    }
    pub fn request_check(&mut self) -> Result<(), String> {
        if !self.snapshot.configured {
            return Err("not_configured".into());
        }
        if self.snapshot.paused {
            return Err("monitor_is_paused".into());
        }
        if self.snapshot.busy || self.requested.is_some() {
            return Err("busy".into());
        }
        self.requested = Some(Trigger::Manual);
        Ok(())
    }
    pub fn prepare(&mut self, now: Duration) -> Option<Request> {
        if self.snapshot.busy {
            return None;
        }
        let trigger = self.requested.take().unwrap_or(Trigger::Timer);
        let request = self.monitor.start(trigger, now).ok()?;
        self.snapshot.busy = true;
        self.snapshot.error = None;
        self.snapshot.next_check_at = None;
        if trigger == Trigger::Manual {
            self.record("check_started", "info", None);
        }
        Some(request)
    }
    pub fn complete(
        &mut self,
        request: &Request,
        result: Result<(ProbeData, Checks), String>,
        now: Duration,
    ) -> Action {
        let old_result = self.snapshot.result.clone();
        let old_tcp_at = self.snapshot.last_tcp_at;
        let old_share_at = self.snapshot.last_share_at;
        let old_requested = self.requested;
        let old_error = self.snapshot.error.clone();
        let (observation, done) = match result {
            Ok((data, done)) => {
                let previous = self.snapshot.result.clone().unwrap_or_default();
                let mut cache = previous.clone();
                if data.mapping.is_some() {
                    cache.mapping = data.mapping.clone();
                    cache.target_matches = data.target_matches;
                }
                if data.tcp.is_some() {
                    cache.tcp = data.tcp;
                    self.snapshot.last_tcp_at = Some(wall_time());
                    if data.tcp == Some(false) {
                        cache.share = None;
                        self.snapshot.last_share_at = None;
                    }
                }
                if data.share.is_some() {
                    cache.share = data.share.clone();
                    self.snapshot.last_share_at = Some(wall_time());
                }
                cache.native_code = data.native_code;
                cache.session_id = data.session_id.or(previous.session_id);
                let mut observation = classify(&self.snapshot.settings, &data, &cache, done);
                if self.verifying_mapping && done.mapping && done.tcp {
                    self.verifying_mapping = false;
                    if observation == Observation::MappingMissing {
                        observation = Observation::Failed {
                            reason: Failure::Mapping,
                            native_code: data.native_code,
                        };
                    }
                }
                // Local changes and TCP recovery require fresh cross-layer evidence.
                if (data.mapping.is_some()
                    && (data.mapping != previous.mapping
                        || data.target_matches != previous.target_matches)
                    && !done.tcp)
                    || (data.tcp == Some(true)
                        && !done.share
                        && (!matches!(cache.mapping.as_deref(), Some("missing" | "disconnected"))
                            && cache.share.is_none()))
                    || (data.share.as_deref() == Some("accessible")
                        && !done.mapping
                        && self.snapshot.state != "healthy")
                {
                    self.requested = Some(Trigger::NetworkChanged);
                }
                self.snapshot.result = Some(cache);
                (observation, done)
            }
            Err(e) => {
                if e != "cancelled" {
                    self.snapshot.error = Some(e.clone());
                }
                (
                    Observation::Failed {
                        reason: if e == "check_timed_out" {
                            Failure::Timeout
                        } else {
                            Failure::Internal
                        },
                        native_code: None,
                    },
                    Checks::NONE,
                )
            }
        };
        let completion = self.monitor.finish(request.ticket, observation, done, now);
        self.snapshot.last_check_at = Some(wall_time());
        let action = match completion {
            Ok(Completion::Decision(decision)) => {
                self.accept(decision, now);
                decision.action
            }
            Ok(Completion::Discarded) => {
                self.snapshot.result = old_result;
                self.snapshot.last_tcp_at = old_tcp_at;
                self.snapshot.last_share_at = old_share_at;
                self.snapshot.error = old_error;
                self.requested = old_requested;
                Action::None
            }
            Err(_) => {
                self.monitor.expire(request.deadline);
                let _ = self.monitor.finish(
                    request.ticket,
                    Observation::NoChange,
                    Checks::NONE,
                    request.deadline,
                );
                self.snapshot.error = Some("worker_output_invalid".into());
                Action::None
            }
        };
        if action == Action::RepairMapping {
            let _ = self.monitor.operation_started(now);
        }
        if action == Action::None {
            self.snapshot.busy = false;
        }
        self.sync(now);
        action
    }
    pub fn complete_operation(
        &mut self,
        result: Result<protocol::Operation, String>,
        now: Duration,
    ) {
        if !self.snapshot.paused {
            match result {
                Ok(op)
                    if matches!(
                        op.status.as_str(),
                        "no_action" | "mapping_connected" | "mapping_removed"
                    ) =>
                {
                    self.record(&op.status, "info", op.native_code);
                    if op.status != "mapping_removed" {
                        self.verifying_mapping = self.snapshot.state == "repairing";
                        self.requested = Some(Trigger::Manual);
                    }
                }
                outcome => {
                    let (reason, code) = match outcome {
                        Ok(op) => (
                            failure(op.reason.as_deref().unwrap_or("mapping")),
                            op.native_code,
                        ),
                        Err(e) => {
                            self.snapshot.error = Some(if e == "check_timed_out" {
                                "operation_outcome_unknown".into()
                            } else {
                                e
                            });
                            (Failure::Mapping, None)
                        }
                    };
                    if let Ok(decision) = self.monitor.operation_failed(reason, code, now) {
                        self.accept(decision, now);
                    }
                }
            }
        }
        self.snapshot.busy = false;
        self.sync(now);
    }
    fn accept(&mut self, decision: Decision, now: Duration) {
        self.sync(now);
        let code = match decision.event {
            CoreEvent::Unchanged => return,
            CoreEvent::Healthy => "healthy",
            CoreEvent::Recovered => "recovered",
            CoreEvent::FailureUnconfirmed => "failure_unconfirmed",
            CoreEvent::Offline => "server_offline",
            CoreEvent::StillOffline => "still_offline",
            CoreEvent::MappingMissing => "mapping_missing",
            CoreEvent::RepairRequested => "repair_requested",
            CoreEvent::Repairing => "repairing",
            CoreEvent::Failed(reason) => failure_name(reason),
        };
        let key = format!("{code}:{:?}", decision.native_code);
        if self.last_event != key {
            self.last_event = key;
            self.record(
                code,
                if matches!(decision.event, CoreEvent::Failed(_) | CoreEvent::Offline) {
                    "error"
                } else if decision.event == CoreEvent::FailureUnconfirmed {
                    "warning"
                } else {
                    "info"
                },
                decision.native_code,
            );
        }
        if self.snapshot.settings.notifications && self.gate.register(&decision, now) {
            self.notice = Some(Notice {
                code: code.into(),
                state: self.snapshot.state.clone(),
                language: self.snapshot.settings.language.clone(),
            });
        }
    }
    fn sync(&mut self, now: Duration) {
        let snap = self.monitor.snapshot();
        let state = state_name(snap.state);
        if self.snapshot.state != state {
            self.snapshot.state = state.into();
            self.snapshot.last_change_at = Some(wall_time());
        }
        self.snapshot.consecutive_failures = snap.consecutive_failures;
        if self.snapshot.paused || !self.snapshot.configured || self.snapshot.busy {
            self.snapshot.next_check_at = None;
        } else if let Some(c) = self.monitor.config() {
            let intervals = Intervals::for_config(c);
            let schedule = self.monitor.schedule();
            let tcp_interval = match snap.state {
                State::Checking => intervals.retry,
                State::ServerOffline => intervals.offline,
                _ => intervals.tcp,
            };
            let next = |last: Option<Duration>, interval: Duration| {
                last.and_then(|value| value.checked_add(interval))
                    .unwrap_or(now)
            };
            let mut due = next(schedule.mapping_at, intervals.mapping)
                .min(next(schedule.tcp_at, tcp_interval));
            if !matches!(snap.state, State::Checking | State::ServerOffline) {
                due = due.min(next(schedule.share_at, intervals.share));
            }
            if self.requested.is_some() {
                due = now;
            }
            self.snapshot.next_check_at =
                Some(wall_time() + due.saturating_sub(now).as_millis() as u64);
        }
    }
    pub fn diagnostics(&self) -> serde_json::Value {
        let events:Vec<_>=self.snapshot.events.iter().take(100).map(|e|serde_json::json!({"atMs":e.at_ms,"code":safe_event(&e.code),"nativeCode":e.native_code})).collect();
        serde_json::json!({"schemaVersion":1,"application":"NetDriveGuard Rust","version":env!("CARGO_PKG_VERSION"),"exportedAt":wall_time(),"state":self.snapshot.state,"paused":self.snapshot.paused,"configured":self.snapshot.configured,"profile":self.snapshot.settings.profile,"removeOffline":self.snapshot.settings.remove_offline,"reconnect":self.snapshot.settings.reconnect,"threshold":self.snapshot.settings.threshold,"tcpTimeoutMs":self.snapshot.settings.tcp_timeout_ms,"overallTimeoutMs":self.snapshot.settings.overall_timeout_ms,"events":events})
    }
}
fn safe_event(code: &str) -> &str {
    match code {
        "healthy"
        | "recovered"
        | "failure_unconfirmed"
        | "server_offline"
        | "still_offline"
        | "mapping_missing"
        | "repair_requested"
        | "repairing"
        | "authentication"
        | "credential_conflict"
        | "share_unavailable"
        | "drive_conflict"
        | "in_use"
        | "internal"
        | "timeout"
        | "mapping"
        | "settings_saved"
        | "monitor_paused"
        | "monitor_resumed"
        | "check_started"
        | "mapping_connected"
        | "mapping_removed"
        | "no_action"
        | "credential_saved"
        | "credential_deleted"
        | "startup_enabled"
        | "startup_disabled" => code,
        _ => "event_unknown",
    }
}
fn classify(
    settings: &Settings,
    current: &ProbeData,
    cache: &ProbeData,
    done: Checks,
) -> Observation {
    let failed = |reason| Observation::Failed {
        reason,
        native_code: current.native_code,
    };
    if current.tcp == Some(false) {
        return Observation::ServerUnreachable;
    }
    if cache.tcp == Some(false) {
        return Observation::NoChange;
    }
    match cache.mapping.as_deref() {
        Some("local") => return failed(Failure::DriveConflict),
        Some("failed") => return failed(Failure::Internal),
        Some("missing" | "disconnected") => {
            return if done.mapping && done.tcp {
                Observation::MappingMissing
            } else {
                Observation::NoChange
            }
        }
        Some("connected") if cache.target_matches == Some(false) => {
            return if settings.reconnect {
                if done.mapping && done.tcp {
                    Observation::MappingMissing
                } else {
                    Observation::NoChange
                }
            } else {
                failed(Failure::DriveConflict)
            }
        }
        _ => {}
    }
    if done.share {
        match current.share.as_deref() {
            Some("authentication") => {
                return failed(if current.native_code == Some(1219) {
                    Failure::CredentialConflict
                } else {
                    Failure::Authentication
                })
            }
            Some("unavailable") => return failed(Failure::ShareUnavailable),
            Some("failed") => return failed(Failure::Internal),
            _ => {}
        }
    }
    if done.mapping
        && done.share
        && current.share.as_deref() == Some("accessible")
        && cache.mapping.as_deref() == Some("connected")
        && cache.target_matches == Some(true)
        && cache.tcp == Some(true)
    {
        Observation::Healthy
    } else {
        Observation::NoChange
    }
}
