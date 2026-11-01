use crate::{
    credentials::{self, Secret},
    query_mapping, shares_equivalent,
};
use net_drive_guard_core::{
    config::Config,
    mapping::{Mapping, MappingBackend, MappingError},
};
use std::ptr;
use windows_sys::Win32::NetworkManagement::WNet::{
    WNetAddConnection2W, WNetCancelConnection2W, CONNECT_UPDATE_PROFILE, NETRESOURCEW,
    RESOURCETYPE_DISK,
};

pub struct Backend;
fn error(e: std::io::Error) -> MappingError {
    MappingError::windows(e.raw_os_error().unwrap_or(13) as u32)
}
impl MappingBackend for Backend {
    type Credentials = Option<Secret>;
    fn query(&mut self, drive: &str) -> Mapping {
        query_mapping(drive)
    }
    fn equivalent(&mut self, left: &str, right: &str) -> Result<bool, MappingError> {
        shares_equivalent(left, right).map_err(error)
    }
    fn credentials(&mut self, config: &Config) -> Result<Self::Credentials, MappingError> {
        credentials::read(config).map_err(error)
    }
    fn disconnect_non_forcibly(&mut self, drive: &str) -> Result<(), MappingError> {
        let name: Vec<u16> = drive.encode_utf16().chain(Some(0)).collect();
        let previous = crate::native::dos_target(drive);
        // SAFETY: owned NUL terminated drive; never disconnect by a server name,
        // and never force-close another application's open files.
        let code = unsafe { WNetCancelConnection2W(name.as_ptr(), CONNECT_UPDATE_PROFILE, 0) };
        if code == 0 || code == 2250 {
            clear_orphan(drive, &name, previous.as_deref()).map_err(error)
        } else {
            Err(MappingError::windows(code))
        }
    }
    fn connect(
        &mut self,
        config: &Config,
        credential: &Self::Credentials,
    ) -> Result<(), MappingError> {
        let mut drive: Vec<u16> = config.drive().encode_utf16().chain(Some(0)).collect();
        let mut share: Vec<u16> = config.share().encode_utf16().chain(Some(0)).collect();
        let resource = NETRESOURCEW {
            dwType: RESOURCETYPE_DISK,
            lpLocalName: drive.as_mut_ptr(),
            lpRemoteName: share.as_mut_ptr(),
            ..Default::default()
        };
        let (user, password) = credential
            .as_ref()
            .map(|c| (c.user.as_ptr(), c.password.as_ptr()))
            .unwrap_or((ptr::null(), ptr::null()));
        // SAFETY: strings and optional secret are valid for this synchronous call.
        // Null credentials use the caller's logon session. No interactive prompt.
        let code =
            unsafe { WNetAddConnection2W(&resource, password, user, CONNECT_UPDATE_PROFILE) };
        if code == 0 {
            Ok(())
        } else {
            Err(MappingError::windows(code))
        }
    }
}

/// WNet may remove only the persistent record while leaving an orphaned DOS
/// link (1203 followed by 85). Never force an active SMB connection closed.
fn clear_orphan(drive: &str, name: &[u16], previous: Option<&str>) -> std::io::Result<()> {
    use windows_sys::Win32::{
        NetworkManagement::{
            NetManagement::{NetApiBufferFree, NetUseGetInfo},
            WNet::WNetGetConnectionW,
        },
        Storage::FileSystem::{
            DefineDosDeviceW, DDD_EXACT_MATCH_ON_REMOVE, DDD_RAW_TARGET_PATH, DDD_REMOVE_DEFINITION,
        },
    };
    let Some(previous) = previous else {
        return Ok(());
    };
    if crate::native::dos_target(drive).as_deref() != Some(previous)
        || crate::native::redirector_target(drive).is_none()
    {
        return Ok(());
    }
    let mut buffer = vec![0u16; 32768];
    let mut n = buffer.len() as u32;
    // SAFETY: name is NUL-terminated; buffer length matches the declared size.
    if unsafe { WNetGetConnectionW(name.as_ptr(), buffer.as_mut_ptr(), &mut n) } != 1203 {
        return Ok(());
    }
    let mut info = ptr::null_mut();
    // SAFETY: local query, valid drive name and output pointer; no mapping writes.
    let status = unsafe { NetUseGetInfo(ptr::null(), name.as_ptr(), 0, &mut info) };
    if !info.is_null() {
        // SAFETY: NetUseGetInfo allocated this buffer; it is freed exactly once.
        unsafe {
            NetApiBufferFree(info.cast());
        }
    }
    if status != 2250 {
        return Ok(());
    }
    // Recheck after the provider call, then remove only the exact observed link.
    if crate::native::dos_target(drive).as_deref() != Some(previous) {
        return Ok(());
    }
    let target: Vec<u16> = previous.encode_utf16().chain([0]).collect();
    // SAFETY: both strings are NUL-terminated. This is an exact-match removal
    // of a confirmed orphan in the caller's logon namespace, not a forced close.
    if unsafe {
        DefineDosDeviceW(
            DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE,
            name.as_ptr(),
            target.as_ptr(),
        )
    } == 0
    {
        return Err(std::io::Error::last_os_error());
    }
    Ok(())
}
