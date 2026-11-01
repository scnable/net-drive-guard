//! Windows-only system access is isolated from the unsafe-free core.
//! Native mapping queries can block: call them in a supervised worker process.
//! Writes use validated plans and must execute in a supervised worker.

mod query;
pub mod worker;

#[cfg(windows)]
mod native;
#[cfg(windows)]
pub use native::{is_local_directory, query_mapping, session_id, shares_equivalent};

#[cfg(windows)]
pub mod credentials;
#[cfg(windows)]
pub mod mapping;
pub mod probe;
#[cfg(windows)]
pub mod signals;
#[cfg(windows)]
pub mod startup;

#[cfg(windows)]
pub mod remote;

#[cfg(windows)]
pub mod rdp_client;
