use std::{fmt, time::Duration};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Profile {
    Standard,
    Fast,
    EnergySaving,
    Custom(Duration),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConfigError {
    Drive,
    Share,
    OfflineThreshold,
    TcpTimeout,
    OverallTimeout,
    CustomInterval,
}

impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::Drive => "drive must be an ASCII letter followed by a colon",
            Self::Share => "share must be a UNC server and share path",
            Self::OfflineThreshold => "offline threshold must be between 2 and 10",
            Self::TcpTimeout => "TCP timeout must be between 100 ms and 10 s",
            Self::OverallTimeout => "overall timeout must exceed TCP timeout and be at most 120 s",
            Self::CustomInterval => "custom interval must be between 1 and 1800 s",
        })
    }
}

impl std::error::Error for ConfigError {}

/// No passwords or usernames belong in this configuration.
/// Private fields ensure invalid values cannot reach the decision engine.
#[derive(Clone, PartialEq, Eq)]
pub struct Config {
    drive: String,
    share: String,
    profile: Profile,
    threshold: u8,
    remove_offline: bool,
    reconnect: bool,
    tcp_timeout: Duration,
    overall_timeout: Duration,
}

// Avoid disclosing a user's server/share through incidental debug logging.
impl fmt::Debug for Config {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Config")
            .field("target", &"<redacted>")
            .field("profile", &self.profile)
            .field("threshold", &self.threshold)
            .field("remove_offline", &self.remove_offline)
            .field("reconnect", &self.reconnect)
            .finish_non_exhaustive()
    }
}

impl Config {
    /// Defaults match the application: both automatic operations are disabled.
    pub fn new(drive: &str, share: &str) -> Result<Self, ConfigError> {
        let bytes = drive.as_bytes();
        if bytes.len() != 2 || !bytes[0].is_ascii_alphabetic() || bytes[1] != b':' {
            return Err(ConfigError::Drive);
        }
        Ok(Self {
            drive: drive.to_ascii_uppercase(),
            share: normalize_share(share).ok_or(ConfigError::Share)?,
            profile: Profile::Standard,
            threshold: 3,
            remove_offline: false,
            reconnect: false,
            tcp_timeout: Duration::from_millis(800),
            overall_timeout: Duration::from_secs(10),
        })
    }

    pub fn with_automatic_operations(mut self, remove_offline: bool, reconnect: bool) -> Self {
        self.remove_offline = remove_offline;
        self.reconnect = reconnect;
        self
    }

    pub fn with_threshold(mut self, threshold: u8) -> Result<Self, ConfigError> {
        if !(2..=10).contains(&threshold) {
            return Err(ConfigError::OfflineThreshold);
        }
        self.threshold = threshold;
        Ok(self)
    }

    pub fn with_profile(mut self, profile: Profile) -> Result<Self, ConfigError> {
        if let Profile::Custom(interval) = profile {
            if !(Duration::from_secs(1)..=Duration::from_secs(1800)).contains(&interval) {
                return Err(ConfigError::CustomInterval);
            }
        }
        self.profile = profile;
        Ok(self)
    }

    pub fn with_timeouts(mut self, tcp: Duration, overall: Duration) -> Result<Self, ConfigError> {
        if !(Duration::from_millis(100)..=Duration::from_secs(10)).contains(&tcp) {
            return Err(ConfigError::TcpTimeout);
        }
        if overall <= tcp || overall > Duration::from_secs(120) {
            return Err(ConfigError::OverallTimeout);
        }
        self.tcp_timeout = tcp;
        self.overall_timeout = overall;
        Ok(self)
    }

    pub fn drive(&self) -> &str {
        &self.drive
    }
    pub fn share(&self) -> &str {
        &self.share
    }
    pub fn profile(&self) -> Profile {
        self.profile
    }
    pub fn threshold(&self) -> u8 {
        self.threshold
    }
    pub fn remove_offline(&self) -> bool {
        self.remove_offline
    }
    pub fn reconnect(&self) -> bool {
        self.reconnect
    }
    pub fn tcp_timeout(&self) -> Duration {
        self.tcp_timeout
    }
    pub fn overall_timeout(&self) -> Duration {
        self.overall_timeout
    }
}

pub(crate) fn normalize_share(value: &str) -> Option<String> {
    let value = value.trim().strip_prefix("\\\\")?;
    let parts: Vec<_> = value
        .split('\\')
        .filter(|s| !s.is_empty())
        .map(str::trim)
        .collect();
    if parts.len() < 2
        || parts.iter().any(|p| {
            p.is_empty()
                || *p == "."
                || *p == ".."
                || p.chars().any(|c| c.is_control() || "/:*?\"<>|".contains(c))
        })
    {
        return None;
    }
    Some(format!("\\\\{}", parts.join("\\")))
}
