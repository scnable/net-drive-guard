use std::time::Duration;

use crate::{
    config::{Config, Profile},
    monitor::State,
};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Checks {
    pub mapping: bool,
    pub tcp: bool,
    pub share: bool,
}

impl Checks {
    pub const ALL: Self = Self {
        mapping: true,
        tcp: true,
        share: true,
    };
    pub const NONE: Self = Self {
        mapping: false,
        tcp: false,
        share: false,
    };
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Trigger {
    Timer,
    Startup,
    Manual,
    Resume,
    ConfigurationSaved,
    NetworkChanged,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Schedule {
    pub mapping_at: Option<Duration>,
    pub tcp_at: Option<Duration>,
    pub share_at: Option<Duration>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Intervals {
    pub mapping: Duration,
    pub tcp: Duration,
    pub retry: Duration,
    pub offline: Duration,
    pub share: Duration,
}

impl Intervals {
    pub fn for_config(config: &Config) -> Self {
        let seconds = Duration::from_secs;
        let (mapping, tcp, offline, share) = match config.profile() {
            Profile::Standard => (seconds(2), seconds(5), seconds(5), seconds(45)),
            Profile::Fast => (seconds(1), seconds(2), seconds(5), seconds(30)),
            Profile::EnergySaving => (seconds(2), seconds(30), seconds(10), seconds(60)),
            Profile::Custom(tcp) => (
                if tcp <= seconds(1) {
                    seconds(1)
                } else {
                    seconds(2)
                },
                tcp,
                tcp.clamp(seconds(5), seconds(10)),
                (tcp * 6).clamp(seconds(30), seconds(60)),
            ),
        };
        Self {
            mapping,
            tcp,
            retry: seconds(1),
            offline,
            share,
        }
    }
}

impl Schedule {
    pub fn due(
        &self,
        config: Option<&Config>,
        state: State,
        trigger: Trigger,
        now: Duration,
    ) -> Checks {
        let Some(config) = config else {
            return Checks::NONE;
        };
        if state == State::Paused {
            return Checks::NONE;
        }
        if trigger != Trigger::Timer {
            return Checks::ALL;
        }
        let intervals = Intervals::for_config(config);
        let tcp_interval = match state {
            State::Checking => intervals.retry,
            State::ServerOffline => intervals.offline,
            _ => intervals.tcp,
        };
        Checks {
            mapping: due(self.mapping_at, intervals.mapping, now),
            tcp: due(self.tcp_at, tcp_interval, now),
            share: !matches!(state, State::Checking | State::ServerOffline)
                && due(self.share_at, intervals.share, now),
        }
    }

    /// Record only layers actually completed by the adapter, not every request.
    pub fn completed(&mut self, checks: Checks, now: Duration) {
        if checks.mapping {
            self.mapping_at = Some(now);
        }
        if checks.tcp {
            self.tcp_at = Some(now);
        }
        if checks.share {
            self.share_at = Some(now);
        }
    }
}

fn due(last: Option<Duration>, interval: Duration, now: Duration) -> bool {
    last.is_none_or(|last| now < last || now - last >= interval)
}
