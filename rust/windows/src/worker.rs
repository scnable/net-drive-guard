//! A single bounded child, with a startup handshake and deterministic reaping.
//! On Windows a non-inheritable job handle also covers abrupt parent death.
//! Commands passed here must wait for `await_start` before doing their work.

use std::{
    io::{self, Read, Write},
    process::{Child, Command, ExitStatus, Stdio},
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant},
};

#[derive(Debug)]
pub struct Outcome {
    pub pid: u32,
    pub status: ExitStatus,
    pub timed_out: bool,
}

struct Reap(Child);

impl Drop for Reap {
    fn drop(&mut self) {
        // Drop the handshake pipe even on assignment/write failures.
        self.0.stdin.take();
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

/// No reader thread or buffered pipe can keep a timed-out worker alive.
/// stdout/stderr use the caller's configured redirection, inheriting by default.
pub fn run(command: &mut Command, timeout: Duration) -> io::Result<Outcome> {
    run_controlled(command, timeout, &[], &AtomicBool::new(false))
}

/// Payload is read immediately after the handshake. Callers cap it at 16 KiB;
/// workers must read to EOF before starting any potentially blocking operation.
pub fn run_controlled(
    command: &mut Command,
    timeout: Duration,
    input: &[u8],
    cancel: &AtomicBool,
) -> io::Result<Outcome> {
    if input.len() > 16384 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "worker input too large",
        ));
    }
    if timeout.is_zero() || timeout > Duration::from_secs(120) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "worker timeout must be in (0, 120s]",
        ));
    }
    if cancel.load(Ordering::Acquire) {
        return Err(io::Error::new(
            io::ErrorKind::Interrupted,
            "worker cancelled",
        ));
    }
    let start = Instant::now();
    #[cfg(windows)]
    let job = crate::native::Job::new()?;
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // Native console workers must not flash a terminal over the desktop UI.
        command.creation_flags(0x08000000); // CREATE_NO_WINDOW
    }
    let mut child = Reap(command.stdin(Stdio::piped()).spawn()?);
    #[cfg(windows)]
    job.assign(&child.0)?;
    let mut pipe = child.0.stdin.take().expect("piped stdin was requested");
    if cancel.load(Ordering::Acquire) {
        return Err(io::Error::new(
            io::ErrorKind::Interrupted,
            "worker cancelled",
        ));
    }
    pipe.write_all(b"G")?;
    pipe.write_all(input)?;
    drop(pipe);
    loop {
        if let Some(status) = child.0.try_wait()? {
            return Ok(Outcome {
                pid: child.0.id(),
                status,
                timed_out: false,
            });
        }
        if start.elapsed() >= timeout || cancel.load(Ordering::Acquire) {
            child.0.kill()?;
            let status = child.0.wait()?;
            return Ok(Outcome {
                pid: child.0.id(),
                status,
                timed_out: true,
            });
        }
        thread::sleep(Duration::from_millis(10).min(timeout.saturating_sub(start.elapsed())));
    }
}

pub fn await_start() -> io::Result<()> {
    let mut byte = [0];
    io::stdin().read_exact(&mut byte)?;
    if byte != *b"G" {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid worker handshake",
        ));
    }
    Ok(())
}
