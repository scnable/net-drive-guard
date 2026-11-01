#![cfg_attr(windows, windows_subsystem = "windows")]
#[cfg(windows)]
mod desktop;
#[cfg(windows)]
mod host;
fn main() {
    #[cfg(windows)]
    {
        if std::env::args().nth(1).as_deref() == Some("--ndg-worker") {
            if net_drive_guard_app::protocol::worker().is_err() {
                std::process::exit(1);
            }
            return;
        }
        if std::env::args().nth(1).as_deref() == Some("--ndg-rdp") {
            let args: Vec<_> = std::env::args().skip(2).collect();
            let result = (|| {
                if args.len() != 5 {
                    return Err("invalid_arguments".to_owned());
                }
                let port = args[1].parse().map_err(|_| "invalid_port")?;
                let width = args[2].parse().map_err(|_| "invalid_size")?;
                let height = args[3].parse().map_err(|_| "invalid_size")?;
                net_drive_guard_windows::rdp_client::run(
                    &args[0], port, width, height, &args[4], false,
                )
            })();
            if result.is_err() {
                std::process::exit(1);
            }
            return;
        }
        desktop::run();
    }
    #[cfg(not(windows))]
    eprintln!("The desktop host requires Windows. Use npm run dev for layout preview.");
}
