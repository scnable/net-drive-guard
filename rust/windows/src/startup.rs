//! Fixed Task Scheduler script; input is JSON on stdin, never interpolated code.
use serde::{Deserialize, Serialize};
use std::{
    io::{self, Write},
    os::windows::process::CommandExt,
    process::{Command, Stdio},
};
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub state: String,
    pub needs_update: bool,
    pub error: Option<String>,
}
pub fn task(action: &str) -> io::Result<Status> {
    if !matches!(action, "query" | "enable" | "disable") {
        return Err(io::Error::from_raw_os_error(87));
    }
    let exe = std::env::current_exe()?;
    let request = serde_json::json!({"action":action,"exe":exe});
    #[cfg(debug_assertions)]
    let request = {
        let mut request = request;
        if let Ok(name) = std::env::var("NDG_TEST_TASK_NAME") {
            if !name.starts_with("ndg-test-")
                || !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
            {
                return Err(io::Error::from_raw_os_error(87));
            }
            request["testName"] = name.into();
        }
        request
    };
    let mut command = Command::new("powershell.exe");
    command
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            include_str!("../scripts/startup.ps1"),
        ])
        .creation_flags(0x08000000)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    let mut child = command.spawn()?;
    let data = serde_json::to_vec(&request)?;
    child
        .stdin
        .take()
        .ok_or_else(|| io::Error::from_raw_os_error(6))?
        .write_all(&data)?;
    let output = child.wait_with_output()?;
    serde_json::from_slice(&output.stdout).map_err(|_| io::Error::from_raw_os_error(13))
}
