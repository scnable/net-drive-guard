//! Blocking probes run inside the supervised process, including DNS and UNC enumeration.
use net_drive_guard_core::{config::Config, schedule::Checks};
use serde::{Deserialize, Serialize};
use std::{
    io,
    net::{TcpStream, ToSocketAddrs},
    time::Instant,
};

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProbeData {
    pub mapping: Option<String>,
    pub target_matches: Option<bool>,
    pub tcp: Option<bool>,
    pub share: Option<String>,
    pub native_code: Option<u32>,
    pub session_id: Option<u32>,
}
/// All candidate addresses share one deadline; DNS is bounded by the worker's
/// outer deadline rather than leaking an unbounded resolver thread.
pub fn tcp(config: &Config) -> io::Result<bool> {
    let server = config.share()[2..]
        .split('\\')
        .next()
        .ok_or_else(|| io::Error::from_raw_os_error(87))?;
    let start = Instant::now();
    let addresses = (server, 445).to_socket_addrs()?;
    for address in addresses {
        let remaining = config.tcp_timeout().saturating_sub(start.elapsed());
        if remaining.is_zero() {
            break;
        }
        if let Ok(stream) = TcpStream::connect_timeout(&address, remaining) {
            drop(stream);
            return Ok(true);
        }
    }
    Ok(false)
}
#[cfg(windows)]
pub fn observe(config: &Config, checks: Checks) -> ProbeData {
    use net_drive_guard_core::mapping::Mapping;
    let mut result = ProbeData {
        session_id: crate::session_id().ok(),
        ..Default::default()
    };
    if checks.mapping {
        result.mapping = Some(
            match crate::query_mapping(config.drive()) {
                Mapping::Missing => "missing",
                Mapping::Connected(remote) => {
                    match crate::shares_equivalent(&remote, config.share()) {
                        Ok(value) => {
                            result.target_matches = Some(value);
                            "connected"
                        }
                        Err(e) => {
                            result.native_code = Some(e.raw_os_error().unwrap_or(13) as u32);
                            "failed"
                        }
                    }
                }
                Mapping::Disconnected(_) => "disconnected",
                Mapping::LocalDrive => "local",
                Mapping::QueryFailed(code) => {
                    result.native_code = Some(code);
                    "failed"
                }
            }
            .into(),
        );
    }
    if checks.tcp {
        result.tcp = Some(match tcp(config) {
            Ok(value) => value,
            Err(e) => {
                result.native_code = Some(e.raw_os_error().unwrap_or(11001) as u32);
                false
            }
        });
    }
    // Never enumerate a server just confirmed unreachable.
    if checks.share && result.tcp != Some(false) {
        let access =
            std::fs::read_dir(config.share()).and_then(|mut entries| match entries.next() {
                Some(entry) => entry.map(|_| ()),
                None => Ok(()),
            });
        result.share = Some(
            match access {
                Ok(()) => "accessible",
                Err(e) => {
                    let code = e.raw_os_error().unwrap_or(13) as u32;
                    result.native_code = Some(code);
                    match code {
                        5 | 86 | 1326 | 1219 => "authentication",
                        2 | 3 | 53 | 67 | 64 | 121 => "unavailable",
                        _ => "failed",
                    }
                }
            }
            .into(),
        );
    }
    result
}
#[cfg(not(windows))]
pub fn observe(_: &Config, _: Checks) -> ProbeData {
    ProbeData {
        mapping: Some("failed".into()),
        native_code: Some(50),
        ..Default::default()
    }
}
