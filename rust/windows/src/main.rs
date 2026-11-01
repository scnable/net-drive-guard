use std::process::ExitCode;

#[cfg(windows)]
fn execute(args: Vec<String>) -> Result<u8, Box<dyn std::error::Error>> {
    use net_drive_guard_core::{config::Config, mapping::Mapping};
    use net_drive_guard_windows::{query_mapping, session_id, shares_equivalent, worker};
    use std::{process::Command, time::Duration};

    let internal = args.first().is_some_and(|a| a == "__query-worker");
    if args.len() < 2 || args.len() > 4 || (!internal && args[0] != "query") {
        return Err(
            "usage: net-drive-guard-inspect query DRIVE [EXPECTED_UNC] [TIMEOUT_MS]".into(),
        );
    }
    let drive = &args[1];
    let expected = args.get(2);
    // Validate before creating a worker, and again in the worker itself.
    Config::new(
        drive,
        expected.map_or(r"\\validation\share", String::as_str),
    )?;
    let timeout = args
        .get(3)
        .map(|s| s.parse::<u64>())
        .transpose()?
        .unwrap_or(3000);
    if !(100..=120000).contains(&timeout) {
        return Err("timeout must be between 100 and 120000 ms".into());
    }
    if !internal {
        let mut child = Command::new(std::env::current_exe()?);
        child.arg("__query-worker").args(&args[1..]);
        let result = worker::run(&mut child, Duration::from_millis(timeout))?;
        if result.timed_out {
            eprintln!(
                "query=timed-out worker_pid={} worker_reaped=true",
                result.pid
            );
            return Ok(124);
        }
        return Ok(result
            .status
            .code()
            .and_then(|c| u8::try_from(c).ok())
            .unwrap_or(1));
    }
    worker::await_start()?;
    println!("mode=read-only session_id={}", session_id()?);
    let mapping = query_mapping(drive);
    match &mapping {
        Mapping::Connected(remote) => {
            println!("mapping=connected");
            if let Some(expected) = expected {
                println!("target_matches={}", shares_equivalent(remote, expected)?);
            }
        }
        Mapping::Disconnected(_) => println!("mapping=disconnected"),
        Mapping::Missing => println!("mapping=missing"),
        Mapping::LocalDrive => println!("mapping=local-drive"),
        Mapping::QueryFailed(code) => {
            println!("mapping=query-failed native_code={code}");
            return Ok(1);
        }
    }
    // A mapping query does not establish SMB/share health.
    println!("share_health=not-checked");
    Ok(0)
}

fn main() -> ExitCode {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() == 1 && matches!(args[0].as_str(), "--help" | "-h") {
        println!("Read-only Windows mapping query (does not connect/disconnect drives).\nUsage: net-drive-guard-inspect query DRIVE [EXPECTED_UNC] [TIMEOUT_MS]\nDefault timeout: 3000 ms. Exit codes: 0=query completed, 1=system error, 2=usage/unsupported, 124=timeout.\nA successful query does not mean the share is healthy.");
        return ExitCode::SUCCESS;
    }
    #[cfg(windows)]
    match execute(args) {
        Ok(code) => ExitCode::from(code),
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(2)
        }
    }
    #[cfg(not(windows))]
    {
        eprintln!(
            "Windows is required for native mapping queries; no simulated result was produced."
        );
        ExitCode::from(2)
    }
}
