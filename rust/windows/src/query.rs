use net_drive_guard_core::mapping::Mapping;

const MORE_DATA: u32 = 234;
const INVALID_DATA: u32 = 13;
const INVALID_PARAMETER: u32 = 87;
const CONNECTION_UNAVAILABLE: u32 = 1201;
const NOT_CONNECTED: u32 = 2250;
const BAD_DEVICE: u32 = 1200;
const MAX_BUFFER: usize = 32768;

/// Keep allocation and native-result handling testable without changing any
/// actual mappings. No retry loop when a provider keeps changing its answer.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn query_with(
    drive: &str,
    mut query: impl FnMut(&[u16], &mut [u16], &mut u32) -> u32,
    mut drive_type: impl FnMut(&[u16]) -> u32,
) -> Mapping {
    let b = drive.as_bytes();
    if b.len() != 2 || !b[0].is_ascii_alphabetic() || b[1] != b':' {
        return Mapping::QueryFailed(INVALID_PARAMETER);
    }
    let local = [u16::from(b[0].to_ascii_uppercase()), u16::from(b':'), 0];
    let mut buffer = vec![0_u16; 512];
    let mut length = buffer.len() as u32;
    let mut code = query(&local, &mut buffer, &mut length);
    if code == MORE_DATA {
        let required = length as usize;
        if required <= buffer.len() || required > MAX_BUFFER {
            return Mapping::QueryFailed(MORE_DATA);
        }
        buffer = vec![0; required];
        code = query(&local, &mut buffer, &mut length);
    }
    match code {
        0 => match decode(&buffer) {
            Some(remote) if !remote.is_empty() => Mapping::Connected(remote),
            _ => Mapping::QueryFailed(INVALID_DATA),
        },
        // The API does not promise a usable remote-name buffer on failure.
        // Preserve disconnected status even when the saved target is unknown.
        CONNECTION_UNAVAILABLE => Mapping::Disconnected(String::new()),
        NOT_CONNECTED | BAD_DEVICE => {
            let root = [local[0], local[1], u16::from(b'\\'), 0];
            match drive_type(&root) {
                1 => Mapping::Missing, // DRIVE_NO_ROOT_DIR
                2 | 3 | 5 | 6 => Mapping::LocalDrive,
                _ => Mapping::QueryFailed(code), // remote/unknown: fail closed
            }
        }
        other => Mapping::QueryFailed(other),
    }
}

/// A provider failure alone never authorizes removing a drive. Require both a
/// remote drive and a valid persistent target owned by the current user.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn unavailable_mapping(kind: u32, remembered: Option<&str>) -> Mapping {
    if matches!(kind, 2 | 3 | 5 | 6) {
        return Mapping::LocalDrive;
    }
    if kind == 4 {
        if let Some(remote) =
            remembered.and_then(|path| net_drive_guard_core::config::Config::new("Z:", path).ok())
        {
            return Mapping::Disconnected(remote.share().to_owned());
        }
    }
    Mapping::QueryFailed(1203)
}

/// Accept only the SMB redirector link for this drive and this logon session.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn lanman_target(drive: &str, target: &str, logon: u64) -> Option<String> {
    let tail = target.strip_prefix(r"\Device\LanmanRedirector\;")?;
    let (identity, path) = tail.split_once('\\')?;
    if identity.len() != 18
        || !identity.get(..2)?.eq_ignore_ascii_case(drive)
        || u64::from_str_radix(identity.get(2..)?, 16).ok()? != logon
    {
        return None;
    }
    let config = net_drive_guard_core::config::Config::new(drive, &format!(r"\\{path}")).ok()?;
    Some(config.share().to_owned())
}

fn decode(buffer: &[u16]) -> Option<String> {
    let end = buffer.iter().position(|&c| c == 0)?;
    String::from_utf16(&buffer[..end]).ok()
}
