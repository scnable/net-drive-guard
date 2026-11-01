use crate::Settings;
use net_drive_guard_core::schedule::Checks;
use net_drive_guard_windows::probe::ProbeData;
use serde::{Deserialize, Serialize};
use std::{
    sync::atomic::AtomicBool,
    time::{Duration, Instant},
};

#[derive(Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Job {
    Probe { layer: String },
    Remediate { remove: bool },
    Startup { action: String },
    CredentialQuery,
    HostProbe { address: String, ports: Vec<u16> },
}
#[derive(Serialize, Deserialize)]
pub struct Request {
    pub settings: Settings,
    pub job: Job,
}
#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Operation {
    pub status: String,
    pub native_code: Option<u32>,
    pub reason: Option<String>,
}
#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Startup {
    pub state: String,
    pub needs_update: bool,
    pub error: Option<String>,
}
#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Reply {
    pub probe: Option<ProbeData>,
    pub host_ports: Option<Vec<bool>>,
    pub operation: Option<Operation>,
    pub startup: Option<Startup>,
    pub credential: Option<bool>,
    pub error: Option<String>,
}
#[cfg(windows)]
pub fn worker() -> Result<(), String> {
    use net_drive_guard_core::{
        mapping::{execute, BlockReason, Execution, Plan},
        monitor::Action,
    };
    use net_drive_guard_windows::{credentials, mapping::Backend, probe, startup, worker};
    use std::io::Read;
    worker::await_start().map_err(|_| "worker_handshake_failed")?;
    let mut input = Vec::new();
    std::io::stdin()
        .take(16385)
        .read_to_end(&mut input)
        .map_err(|_| "worker_input_failed")?;
    if input.len() > 16384 {
        return Err("worker_input_invalid".into());
    }
    let request: Request = serde_json::from_slice(&input).map_err(|_| "worker_input_invalid")?;
    if let Job::Startup { action } = &request.job {
        let reply = match startup::task(action) {
            Ok(s) => Reply {
                startup: Some(Startup {
                    state: s.state,
                    needs_update: s.needs_update,
                    error: s.error,
                }),
                ..Default::default()
            },
            Err(_) => Reply {
                error: Some("startup_failed".into()),
                ..Default::default()
            },
        };
        return serde_json::to_writer(std::io::stdout().lock(), &reply)
            .map_err(|_| "worker_output_failed".into());
    }
    if let Job::HostProbe { address, ports } = &request.job {
        use std::net::{TcpStream, ToSocketAddrs};
        if ports.len() != 3 || address.len() > 253 {
            return Err("host_settings_invalid".into());
        }
        let mut results = Vec::new();
        for port in ports {
            let start = Instant::now();
            let ok = *port != 0
                && (address.as_str(), *port)
                    .to_socket_addrs()
                    .ok()
                    .is_some_and(|mut addresses| {
                        addresses.any(|a| {
                            let remaining =
                                Duration::from_millis(800).saturating_sub(start.elapsed());
                            !remaining.is_zero()
                                && TcpStream::connect_timeout(&a, remaining).is_ok()
                        })
                    });
            results.push(ok);
        }
        return serde_json::to_writer(
            std::io::stdout().lock(),
            &Reply {
                host_ports: Some(results),
                ..Default::default()
            },
        )
        .map_err(|_| "worker_output_failed".into());
    }
    let config = request.settings.validate()?;
    let reply = match request.job {
        Job::HostProbe { .. } => return Err("worker_input_invalid".into()),
        Job::Probe { layer } => {
            let checks = match layer.as_str() {
                "mapping" => Checks {
                    mapping: true,
                    ..Checks::NONE
                },
                "tcp" => Checks {
                    tcp: true,
                    ..Checks::NONE
                },
                "share" => Checks {
                    share: true,
                    ..Checks::NONE
                },
                _ => return Err("worker_input_invalid".into()),
            };
            Reply {
                probe: Some(probe::observe(&config, checks)),
                ..Default::default()
            }
        }
        Job::Remediate { remove } => {
            let action = if remove {
                Action::RemoveMapping
            } else {
                Action::RepairMapping
            };
            let result = execute(&config, action, &mut Backend);
            let operation = match result {
                Ok(Execution::NoAction) => Operation {
                    status: "no_action".into(),
                    ..Default::default()
                },
                Ok(Execution::Applied(plan)) => Operation {
                    status: if plan == Plan::Disconnect {
                        "mapping_removed"
                    } else {
                        "mapping_connected"
                    }
                    .into(),
                    ..Default::default()
                },
                Ok(Execution::Blocked(reason)) => Operation {
                    status: "blocked".into(),
                    native_code: if let BlockReason::QueryFailed(c) = reason {
                        Some(c)
                    } else {
                        None
                    },
                    reason: Some(
                        if matches!(
                            reason,
                            BlockReason::LocalDrive | BlockReason::ChangedDuringPreparation
                        ) {
                            "drive_conflict"
                        } else {
                            "internal"
                        }
                        .into(),
                    ),
                },
                Err(error) => Operation {
                    status: "failed".into(),
                    native_code: error.native_code,
                    reason: Some(crate::failure_name(error.reason).into()),
                },
            };
            Reply {
                operation: Some(operation),
                ..Default::default()
            }
        }
        Job::CredentialQuery => match credentials::read(&config) {
            Ok(value) => Reply {
                credential: Some(value.is_some()),
                ..Default::default()
            },
            Err(_) => Reply {
                error: Some("credential_read_failed".into()),
                ..Default::default()
            },
        },
        Job::Startup { action } => match startup::task(&action) {
            Ok(s) => Reply {
                startup: Some(Startup {
                    state: s.state,
                    needs_update: s.needs_update,
                    error: s.error,
                }),
                ..Default::default()
            },
            Err(_) => Reply {
                error: Some("startup_failed".into()),
                ..Default::default()
            },
        },
    };
    serde_json::to_writer(std::io::stdout().lock(), &reply)
        .map_err(|_| "worker_output_failed".into())
}
#[cfg(windows)]
pub fn run(request: &Request, timeout: Duration, cancel: &AtomicBool) -> Result<Reply, String> {
    use std::{
        io::{Read, Seek, SeekFrom},
        process::{Command, Stdio},
    };
    let mut output = tempfile::tempfile().map_err(|_| "worker_output_failed")?;
    let input = serde_json::to_vec(request).map_err(|_| "worker_input_invalid")?;
    let mut command = Command::new(std::env::current_exe().map_err(|_| "worker_start_failed")?);
    command
        .arg("--ndg-worker")
        .stdout(output.try_clone().map_err(|_| "worker_output_failed")?)
        .stderr(Stdio::null());
    let outcome =
        net_drive_guard_windows::worker::run_controlled(&mut command, timeout, &input, cancel)
            .map_err(|_| {
                if cancel.load(std::sync::atomic::Ordering::Acquire) {
                    "cancelled"
                } else {
                    "worker_failed"
                }
            })?;
    if outcome.timed_out {
        return Err(if cancel.load(std::sync::atomic::Ordering::Acquire) {
            "cancelled"
        } else {
            "check_timed_out"
        }
        .into());
    }
    if !outcome.status.success() {
        return Err("worker_failed".into());
    }
    output
        .seek(SeekFrom::Start(0))
        .map_err(|_| "worker_output_failed")?;
    let mut bytes = Vec::new();
    output
        .take(16385)
        .read_to_end(&mut bytes)
        .map_err(|_| "worker_output_failed")?;
    if bytes.len() > 16384 {
        return Err("worker_output_invalid".into());
    }
    let reply: Reply = serde_json::from_slice(&bytes).map_err(|_| "worker_output_invalid")?;
    if let Some(error) = &reply.error {
        return Err(error.clone());
    }
    Ok(reply)
}
#[cfg(not(windows))]
pub fn run(_: &Request, _: Duration, _: &AtomicBool) -> Result<Reply, String> {
    Err("windows_required".into())
}

/// Each blocking layer has its own supervised process. DNS uses the TCP budget;
/// a hung share cannot hide TCP evidence or spawn unbounded replacement threads.
pub fn probe(
    settings: &Settings,
    checks: Checks,
    cancel: &AtomicBool,
) -> Result<(ProbeData, Checks), String> {
    let config = settings.validate()?;
    let started = Instant::now();
    let mut data = ProbeData::default();
    let mut done = Checks::NONE;
    for (layer, enabled, budget) in [
        ("mapping", checks.mapping, config.overall_timeout()),
        ("tcp", checks.tcp, config.tcp_timeout()),
        ("share", checks.share, Duration::from_secs(5)),
    ] {
        if !enabled || (layer == "share" && data.tcp == Some(false)) {
            continue;
        }
        let remaining = config.overall_timeout().saturating_sub(started.elapsed());
        if remaining.is_zero() {
            return Err("check_timed_out".into());
        }
        let request = Request {
            settings: settings.clone(),
            job: Job::Probe {
                layer: layer.into(),
            },
        };
        match run(&request, budget.min(remaining), cancel) {
            Ok(reply) => {
                let result = reply.probe.ok_or("worker_output_invalid")?;
                data.session_id = result.session_id;
                match layer {
                    "mapping" => {
                        data.mapping = result.mapping;
                        data.target_matches = result.target_matches;
                        done.mapping = true
                    }
                    "tcp" => {
                        data.tcp = result.tcp;
                        done.tcp = true
                    }
                    _ => {
                        data.share = result.share;
                        done.share = true
                    }
                }
                if result.native_code.is_some() {
                    data.native_code = result.native_code;
                }
            }
            Err(e) if e == "check_timed_out" && layer == "tcp" => {
                data.tcp = Some(false);
                done.tcp = true;
                data.native_code = Some(1460);
            }
            Err(e) if e == "check_timed_out" && layer == "share" => {
                data.share = Some("unavailable".into());
                done.share = true;
                data.native_code = Some(1460);
            }
            Err(e) => return Err(e),
        }
    }
    Ok((data, done))
}
