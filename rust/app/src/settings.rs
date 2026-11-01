use net_drive_guard_core::{
    config::{Config, Profile},
    schedule::Intervals,
};
use serde::{Deserialize, Serialize};
use std::time::Duration;
#[derive(Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct Settings {
    pub drive: String,
    pub share: String,
    pub profile: String,
    pub interval_secs: u64,
    pub threshold: u8,
    pub tcp_timeout_ms: u64,
    pub overall_timeout_ms: u64,
    pub remove_offline: bool,
    pub reconnect: bool,
    pub language: String,
    pub notifications: bool,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            drive: "Z:".into(),
            share: String::new(),
            profile: "standard".into(),
            interval_secs: 5,
            threshold: 3,
            tcp_timeout_ms: 800,
            overall_timeout_ms: 10000,
            remove_offline: false,
            reconnect: false,
            language: "zh".into(),
            notifications: true,
        }
    }
}
impl Settings {
    pub fn validate(&self) -> Result<Config, String> {
        if self.share.len() > 8192 {
            return Err("invalid_target".into());
        }
        if !matches!(self.language.as_str(), "zh" | "en") {
            return Err("invalid_language".into());
        }
        let profile = match self.profile.as_str() {
            "standard" => Profile::Standard,
            "fast" => Profile::Fast,
            "energy" => Profile::EnergySaving,
            "custom" => Profile::Custom(Duration::from_secs(self.interval_secs)),
            _ => return Err("invalid_profile".into()),
        };
        let config = Config::new(&self.drive, &self.share)
            .map_err(|_| "invalid_target")?
            .with_profile(profile)
            .map_err(|_| "invalid_interval")?
            .with_automatic_operations(self.remove_offline, self.reconnect);
        if matches!(profile, Profile::Custom(_)) {
            config
                .with_threshold(self.threshold)
                .map_err(|_| "invalid_threshold")?
                .with_timeouts(
                    Duration::from_millis(self.tcp_timeout_ms),
                    Duration::from_millis(self.overall_timeout_ms),
                )
                .map_err(|_| "invalid_timeout".into())
        } else {
            Ok(config)
        }
    }
    pub fn canonical(mut self) -> Result<Self, String> {
        let c = self.validate()?;
        self.drive = c.drive().into();
        self.share = c.share().into();
        self.interval_secs = Intervals::for_config(&c).tcp.as_secs();
        self.threshold = c.threshold();
        self.tcp_timeout_ms = c.tcp_timeout().as_millis() as u64;
        self.overall_timeout_ms = c.overall_timeout().as_millis() as u64;
        Ok(self)
    }
}
/// Reads the last C# JSON schema, not legacy executable scripts. A malformed or
/// unsupported document is preserved and never silently replaced with defaults.
pub fn decode(data: &[u8]) -> Result<Settings, String> {
    let value: serde_json::Value = serde_json::from_slice(data).map_err(|_| "config_invalid")?;
    if let Some(version) = value.get("version") {
        if version.as_u64() != Some(1) {
            return Err("config_version_unsupported".into());
        }
        return serde_json::from_value::<Settings>(
            value.get("settings").cloned().ok_or("config_invalid")?,
        )
        .map_err(|_| "config_invalid".into())
        .and_then(Settings::canonical);
    }
    if value.get("schemaVersion").and_then(|v| v.as_u64()) != Some(1) {
        return Err("config_version_unsupported".into());
    }
    let old = value.get("configuration").ok_or("config_invalid")?;
    let text = |key: &str| {
        old.get(key)
            .and_then(|v| v.as_str())
            .ok_or_else(|| "config_invalid".to_string())
    };
    let number = |key: &str, default: u64| old.get(key).and_then(|v| v.as_u64()).unwrap_or(default);
    let profile = match number("profile", 0) {
        0 => "standard",
        1 => "fast",
        2 => "energy",
        3 => "custom",
        _ => return Err("invalid_profile".into()),
    };
    let duration = |key: &str, default: u64| -> Result<u64, String> {
        let Some(v) = old.get(key).filter(|v| !v.is_null()) else {
            return Ok(default);
        };
        let parts: Vec<_> = v.as_str().ok_or("config_invalid")?.split(':').collect();
        if parts.len() != 3 {
            return Err("config_invalid".into());
        }
        let hour = parts[0].parse::<u64>().map_err(|_| "config_invalid")?;
        let min = parts[1].parse::<u64>().map_err(|_| "config_invalid")?;
        let sec = parts[2].parse::<f64>().map_err(|_| "config_invalid")?;
        if hour > 48 || min >= 60 || !sec.is_finite() || !(0.0..60.0).contains(&sec) {
            return Err("config_invalid".into());
        }
        Ok(((hour * 3600 + min * 60) as f64 * 1000.0 + sec * 1000.0).round() as u64)
    };
    Settings {
        drive: text("driveLetter")?.into(),
        share: text("sharePath")?.into(),
        profile: profile.into(),
        interval_secs: duration("customTcpInterval", 5000)? / 1000,
        threshold: u8::try_from(number("offlineFailureThreshold", 3))
            .map_err(|_| "invalid_threshold")?,
        tcp_timeout_ms: duration("tcpTimeout", 800)?,
        overall_timeout_ms: duration("overallCheckTimeout", 10000)?,
        remove_offline: old
            .get("removeMappingWhenOffline")
            .and_then(|v| v.as_bool())
            .unwrap_or(false),
        reconnect: old
            .get("repairMissingMapping")
            .and_then(|v| v.as_bool())
            .unwrap_or(false),
        language: if old.get("language").and_then(|v| v.as_str()) == Some("en") {
            "en"
        } else {
            "zh"
        }
        .into(),
        notifications: true,
    }
    .canonical()
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Preferences {
    pub language: String,
    pub notifications: bool,
}
impl Preferences {
    pub fn validate(&self) -> Result<(), String> {
        if matches!(self.language.as_str(), "zh" | "en") {
            Ok(())
        } else {
            Err("invalid_language".into())
        }
    }
    pub fn from_settings(settings: &Settings) -> Self {
        Self {
            language: settings.language.clone(),
            notifications: settings.notifications,
        }
    }
}
