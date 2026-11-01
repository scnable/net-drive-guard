use crate::{
    config::{normalize_share, Config},
    monitor::{Action, Failure},
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Mapping {
    Missing,
    Connected(String),
    /// Persistent disconnected mapping, confirmed by Windows or by a remote
    /// drive plus its validated current-user persistent target.
    /// The target may be empty: WNet does not guarantee an output name on error.
    Disconnected(String),
    LocalDrive,
    QueryFailed(u32),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BlockReason {
    InvalidRemote,
    LocalDrive,
    QueryFailed(u32),
    ChangedDuringPreparation,
}

/// A decision only. Replace means acquire credentials first, then disconnect
/// non-forcibly and connect the CURRENT target. Never fall back to the old path.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Plan {
    None,
    Connect,
    Disconnect,
    Replace,
    Blocked(BlockReason),
}

pub fn plan(config: &Config, action: Action, mapping: &Mapping) -> Plan {
    plan_with(config, action, mapping, |left, right| {
        Ok(left.eq_ignore_ascii_case(right))
    })
}

pub fn plan_with(
    config: &Config,
    action: Action,
    mapping: &Mapping,
    mut equivalent: impl FnMut(&str, &str) -> Result<bool, MappingError>,
) -> Plan {
    if action == Action::None
        || (action == Action::RemoveMapping && !config.remove_offline())
        || (action == Action::RepairMapping && !config.reconnect())
    {
        return Plan::None;
    }
    match mapping {
        Mapping::LocalDrive => Plan::Blocked(BlockReason::LocalDrive),
        Mapping::QueryFailed(code) => Plan::Blocked(BlockReason::QueryFailed(*code)),
        Mapping::Missing => {
            if action == Action::RepairMapping {
                Plan::Connect
            } else {
                Plan::None
            }
        }
        Mapping::Disconnected(_) => {
            // The adapter confirms the configured drive is a disconnected persistent
            // mapping even when its old target name could not be recovered.
            // Reconnection always uses the validated current config target.
            if action == Action::RemoveMapping {
                Plan::Disconnect
            } else {
                Plan::Replace
            }
        }
        Mapping::Connected(remote) => {
            let Some(remote) = normalize_share(remote) else {
                return Plan::Blocked(BlockReason::InvalidRemote);
            };
            if action == Action::RemoveMapping {
                return Plan::Disconnect;
            }
            match equivalent(&remote, config.share()) {
                Ok(true) => Plan::None,
                Ok(false) => Plan::Replace,
                Err(error) => {
                    Plan::Blocked(BlockReason::QueryFailed(error.native_code.unwrap_or(13)))
                }
            }
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MappingError {
    pub reason: Failure,
    pub native_code: Option<u32>,
}

impl MappingError {
    pub fn windows(code: u32) -> Self {
        Self {
            reason: Failure::from_windows_error(code),
            native_code: Some(code),
        }
    }
}

/// Implemented by an OS adapter or an in-memory fixture. Credentials stay opaque
/// to the core; the adapter must wipe secret buffers when they are dropped.
/// Query must describe only `drive`, including local disk conflicts and 1201.
/// Implementations must bound blocking calls; this synchronous sequencing helper
/// must run under the host's remediation worker, not its UI/event-loop thread.
pub trait MappingBackend {
    type Credentials;
    fn equivalent(&mut self, left: &str, right: &str) -> Result<bool, MappingError> {
        Ok(left.eq_ignore_ascii_case(right))
    }
    fn query(&mut self, drive: &str) -> Mapping;
    fn credentials(&mut self, config: &Config) -> Result<Self::Credentials, MappingError>;
    fn disconnect_non_forcibly(&mut self, drive: &str) -> Result<(), MappingError>;
    fn connect(
        &mut self,
        config: &Config,
        credentials: &Self::Credentials,
    ) -> Result<(), MappingError>;
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Execution {
    NoAction,
    Blocked(BlockReason),
    /// The adapter reported success; a fresh probe must still verify health.
    Applied(Plan),
}

/// Re-query at execution time and obtain current-target credentials BEFORE
/// disconnecting. Never force a disconnect or reconnect a superseded target.
pub fn execute(
    config: &Config,
    action: Action,
    backend: &mut impl MappingBackend,
) -> Result<Execution, MappingError> {
    if action == Action::None
        || (action == Action::RemoveMapping && !config.remove_offline())
        || (action == Action::RepairMapping && !config.reconnect())
    {
        return Ok(Execution::NoAction);
    }
    let current = backend.query(config.drive());
    let operation = plan_with(config, action, &current, |a, b| backend.equivalent(a, b));
    match operation {
        Plan::None => Ok(Execution::NoAction),
        Plan::Blocked(reason) => Ok(Execution::Blocked(reason)),
        Plan::Disconnect => {
            backend.disconnect_non_forcibly(config.drive())?;
            Ok(Execution::Applied(operation))
        }
        Plan::Connect | Plan::Replace => {
            let credentials = backend.credentials(config)?;
            // Credential retrieval may yield to another actor. Never disconnect
            // a mapping that changed while preparing this operation.
            if backend.query(config.drive()) != current {
                return Ok(Execution::Blocked(BlockReason::ChangedDuringPreparation));
            }
            if operation == Plan::Replace {
                backend.disconnect_non_forcibly(config.drive())?;
            }
            backend.connect(config, &credentials)?;
            Ok(Execution::Applied(operation))
        }
    }
}
