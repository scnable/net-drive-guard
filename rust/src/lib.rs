//! Pure decisions and a single-flight check lifecycle. No network or OS writes.
//!
//! Adapters must distinguish TCP reachability, share access and verified mapping
//! health. An action here is a request, never proof of successful remediation.

pub mod config;
pub mod mapping;
pub mod monitor;
pub mod runtime;
pub mod schedule;
