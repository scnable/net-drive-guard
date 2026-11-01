use std::{collections::HashMap, time::Duration};

use crate::config::Config;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum State {
    NotConfigured,
    Checking,
    Healthy,
    ServerOffline,
    MappingMissing,
    Repairing,
    AuthenticationFailed,
    ShareUnavailable,
    DriveConflict,
    Paused,
    InternalError,
    MappingFailed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Failure {
    Authentication,
    CredentialConflict,
    ShareUnavailable,
    DriveConflict,
    InUse,
    Internal,
    Timeout,
    Mapping,
}

impl Failure {
    pub fn from_windows_error(code: u32) -> Self {
        match code {
            5 | 86 | 1326 => Self::Authentication,
            1219 => Self::CredentialConflict,
            53 | 67 => Self::ShareUnavailable,
            2401 | 2404 => Self::InUse,
            _ => Self::Mapping,
        }
    }

    pub fn state(self) -> State {
        match self {
            Self::Authentication => State::AuthenticationFailed,
            Self::ShareUnavailable => State::ShareUnavailable,
            Self::DriveConflict => State::DriveConflict,
            Self::Internal | Self::Timeout => State::InternalError,
            Self::CredentialConflict | Self::InUse | Self::Mapping => State::MappingFailed,
        }
    }
}

/// A TCP-only success must be NoChange, never Healthy. Healthy requires the
/// adapter to verify the configured mapping and share, not just the SMB port.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Observation {
    NoChange,
    Healthy,
    ServerUnreachable,
    MappingMissing,
    Repairing,
    Failed {
        reason: Failure,
        native_code: Option<u32>,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Action {
    None,
    RemoveMapping,
    RepairMapping,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Event {
    Unchanged,
    Healthy,
    Recovered,
    FailureUnconfirmed,
    Offline,
    StillOffline,
    MappingMissing,
    RepairRequested,
    Repairing,
    Failed(Failure),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Snapshot {
    pub state: State,
    pub stable: State,
    pub consecutive_failures: u8,
    pub transitioned_at: Duration,
    pub observed_at: Duration,
}

impl Snapshot {
    pub fn initial(now: Duration) -> Self {
        Self {
            state: State::NotConfigured,
            stable: State::NotConfigured,
            consecutive_failures: 0,
            transitioned_at: now,
            observed_at: now,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Decision {
    pub snapshot: Snapshot,
    pub action: Action,
    pub notify: bool,
    pub event: Event,
    pub native_code: Option<u32>,
}

/// `now` is elapsed monotonic time supplied by the host, which keeps tests free
/// of sleeping and prevents wall-clock changes from affecting operation timing.
pub fn evaluate(
    config: &Config,
    previous: Snapshot,
    observation: Observation,
    now: Duration,
) -> Decision {
    let mut result = Decision {
        snapshot: previous,
        action: Action::None,
        notify: false,
        event: Event::Unchanged,
        native_code: None,
    };
    result.snapshot.observed_at = now;
    let mut stable = true;
    let next = match observation {
        Observation::NoChange => return result,
        Observation::Healthy => {
            result.notify = !matches!(
                previous.stable,
                State::Healthy | State::NotConfigured | State::Checking | State::Paused
            ) || matches!(previous.state, State::MappingMissing | State::Repairing);
            result.event = if result.notify {
                Event::Recovered
            } else {
                Event::Healthy
            };
            State::Healthy
        }
        Observation::ServerUnreachable => {
            result.snapshot.consecutive_failures = previous.consecutive_failures.saturating_add(1);
            if result.snapshot.consecutive_failures < config.threshold() {
                stable = false;
                result.event = Event::FailureUnconfirmed;
                State::Checking
            } else {
                result.notify = previous.stable != State::ServerOffline;
                if result.notify && config.remove_offline() {
                    result.action = Action::RemoveMapping;
                }
                result.event = if result.notify {
                    Event::Offline
                } else {
                    Event::StillOffline
                };
                State::ServerOffline
            }
        }
        Observation::MappingMissing => {
            // Retain the last confirmed fault through automatic retries, so the
            // same failed operation does not produce a fresh alert every cycle.
            stable = !config.reconnect();
            let repair = config.reconnect()
                && !matches!(previous.state, State::MappingMissing | State::Repairing);
            result.action = if repair {
                Action::RepairMapping
            } else {
                Action::None
            };
            result.event = if repair {
                Event::RepairRequested
            } else {
                Event::MappingMissing
            };
            State::MappingMissing
        }
        Observation::Repairing => {
            stable = false;
            result.event = Event::Repairing;
            State::Repairing
        }
        Observation::Failed {
            reason,
            native_code,
        } => {
            let state = reason.state();
            stable = !(state == State::InternalError && previous.state == State::Checking);
            result.notify = stable && previous.stable != state;
            result.event = Event::Failed(reason);
            result.native_code = native_code;
            state
        }
    };
    if !matches!(observation, Observation::ServerUnreachable) {
        result.snapshot.consecutive_failures = 0;
    }
    if next != previous.state {
        result.snapshot.transitioned_at = now;
    }
    result.snapshot.state = next;
    if stable {
        result.snapshot.stable = next;
    }
    result
}

/// Bounded typed event keys, with independent cooldown for recovery and errors.
/// The host owns this value, so notification registration is serialized.
pub struct NotificationGate {
    cooldown: Duration,
    last: HashMap<Event, Duration>,
}

impl NotificationGate {
    pub fn new(cooldown: Duration) -> Self {
        Self {
            cooldown,
            last: HashMap::new(),
        }
    }

    pub fn register(&mut self, decision: &Decision, now: Duration) -> bool {
        if !decision.notify {
            return false;
        }
        if let Some(&last) = self.last.get(&decision.event) {
            if now >= last && now - last < self.cooldown {
                return false;
            }
        }
        self.last.insert(decision.event, now);
        true
    }

    pub fn reset(&mut self, event: Event) {
        self.last.remove(&event);
    }
}
