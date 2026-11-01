use std::{
    io,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    process::Child,
    ptr,
};

use net_drive_guard_core::{config::Config, mapping::Mapping};
use windows_sys::Win32::{
    Globalization::{CompareStringOrdinal, CSTR_EQUAL},
    NetworkManagement::WNet::WNetGetConnectionW,
    Security::{GetTokenInformation, TokenStatistics, TOKEN_QUERY, TOKEN_STATISTICS},
    Storage::FileSystem::{GetDriveTypeW, QueryDosDeviceW},
    System::{
        JobObjects::{
            AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
            SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        },
        Registry::{RegGetValueW, HKEY_CURRENT_USER, RRF_RT_REG_SZ},
        RemoteDesktop::ProcessIdToSessionId,
        Threading::{GetCurrentProcess, OpenProcessToken},
    },
};

/// Queries the calling logon session, not another user's desktop mappings.
/// This may block inside a network provider; use the bounded diagnostic CLI.
pub fn query_mapping(drive: &str) -> Mapping {
    let mapping = crate::query::query_with(
        drive,
        |local, buffer, length| {
            // SAFETY: local is NUL-terminated; buffer is writable for *length UTF-16
            // units. The helper bounds allocation and keeps all borrows alive.
            unsafe { WNetGetConnectionW(local.as_ptr(), buffer.as_mut_ptr(), length) }
        },
        |root| {
            // SAFETY: root is an owned, NUL-terminated drive-root string.
            unsafe { GetDriveTypeW(root.as_ptr()) }
        },
    );
    if mapping != Mapping::QueryFailed(1203) {
        return mapping;
    }
    let root: Vec<u16> = format!("{drive}\\").encode_utf16().chain([0]).collect();
    // SAFETY: root is a live, NUL-terminated drive-root string.
    let kind = unsafe { GetDriveTypeW(root.as_ptr()) };
    let remembered = if kind == 4 {
        redirector_target(drive).or_else(|| remembered_target(drive))
    } else {
        None
    };
    crate::query::unavailable_mapping(kind, remembered.as_deref())
}

pub(crate) fn dos_target(drive: &str) -> Option<String> {
    let name: Vec<u16> = drive.encode_utf16().chain([0]).collect();
    let mut buffer = vec![0u16; 32768];
    // SAFETY: NUL-terminated name and bounded writable output buffer.
    let n = unsafe { QueryDosDeviceW(name.as_ptr(), buffer.as_mut_ptr(), buffer.len() as u32) };
    if n == 0 {
        return None;
    }
    let end = buffer[..n as usize].iter().position(|&c| c == 0)?;
    String::from_utf16(&buffer[..end]).ok()
}

pub(crate) fn redirector_target(drive: &str) -> Option<String> {
    let target = dos_target(drive)?;
    let mut token = ptr::null_mut();
    // SAFETY: process pseudo-handle is valid; token receives an owned handle.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return None;
    }
    // SAFETY: successful OpenProcessToken returned a unique owned handle.
    let token = unsafe { OwnedHandle::from_raw_handle(token) };
    let mut stats = TOKEN_STATISTICS::default();
    let mut size = 0;
    // SAFETY: token is live; output type and size match TokenStatistics.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenStatistics,
            (&mut stats as *mut TOKEN_STATISTICS).cast(),
            std::mem::size_of_val(&stats) as u32,
            &mut size,
        )
    } == 0
    {
        return None;
    }
    let logon = ((stats.AuthenticationId.HighPart as u32 as u64) << 32)
        | stats.AuthenticationId.LowPart as u64;
    crate::query::lanman_target(drive, &target, logon)
}

fn remembered_target(drive: &str) -> Option<String> {
    // query_with validated the drive before returning error 1203.
    let key: Vec<u16> = format!("Network\\{}", &drive[..1])
        .encode_utf16()
        .chain([0])
        .collect();
    let name: Vec<u16> = "RemotePath".encode_utf16().chain([0]).collect();
    let mut buffer = vec![0u16; 32768];
    let mut bytes = (buffer.len() * 2) as u32;
    // SAFETY: names are NUL-terminated; buffer size agrees with byte count.
    // RegGetValue validates REG_SZ and supplies its terminator. No writes occur.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            key.as_ptr(),
            name.as_ptr(),
            RRF_RT_REG_SZ,
            ptr::null_mut(),
            buffer.as_mut_ptr().cast(),
            &mut bytes,
        )
    };
    if status != 0 || bytes < 2 || bytes % 2 != 0 || bytes as usize > buffer.len() * 2 {
        return None;
    }
    let buffer = &buffer[..bytes as usize / 2];
    let end = buffer.iter().position(|&c| c == 0)?;
    String::from_utf16(&buffer[..end]).ok()
}

/// Windows ordinal, case-insensitive comparison after shared UNC validation.
/// Returns errors rather than treating an API/validation failure as inequality.
pub fn shares_equivalent(left: &str, right: &str) -> io::Result<bool> {
    let normalize = |value| {
        Config::new("Z:", value)
            .map(|c| c.share().encode_utf16().collect::<Vec<_>>())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid UNC share"))
    };
    let left = normalize(left)?;
    let right = normalize(right)?;
    let len = |v: &[u16]| {
        i32::try_from(v.len())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "share is too long"))
    };
    let (left_len, right_len) = (len(&left)?, len(&right)?);
    // SAFETY: lengths are checked and exactly describe the live UTF-16 buffers.
    let result =
        unsafe { CompareStringOrdinal(left.as_ptr(), left_len, right.as_ptr(), right_len, 1) };
    if result == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(result == CSTR_EQUAL)
    }
}

pub fn session_id() -> io::Result<u32> {
    let mut session = 0;
    // SAFETY: output is a valid pointer and the ID identifies this process.
    if unsafe { ProcessIdToSessionId(std::process::id(), &mut session) } == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(session)
    }
}

/// Non-inheritable handle: parent death closes the last handle and terminates
/// workers. Workers wait on stdin until assignment succeeds, closing the race
/// between spawning a process and putting it under job supervision.
pub(crate) struct Job(OwnedHandle);

impl Job {
    pub(crate) fn new() -> io::Result<Self> {
        // SAFETY: null attributes request a non-inheritable, unnamed job.
        let raw = unsafe { CreateJobObjectW(ptr::null(), ptr::null()) };
        if raw.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: creation returned a unique valid handle; OwnedHandle closes it.
        let handle = unsafe { OwnedHandle::from_raw_handle(raw) };
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        // SAFETY: the handle is live; pointer and size match the specified class.
        if unsafe {
            SetInformationJobObject(
                handle.as_raw_handle(),
                JobObjectExtendedLimitInformation,
                (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                std::mem::size_of_val(&limits) as u32,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(Self(handle))
    }

    pub(crate) fn assign(&self, child: &Child) -> io::Result<()> {
        // SAFETY: both handles stay alive through the call; Child owns its handle.
        if unsafe { AssignProcessToJobObject(self.0.as_raw_handle(), child.as_raw_handle()) } == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(())
        }
    }
}

/// Data and logs must not live on a drive that the application can disconnect.
pub fn is_local_directory(path: &std::path::Path) -> bool {
    let text = path.to_string_lossy();
    let Some(prefix) = text.get(..2) else {
        return false;
    };
    if !prefix.as_bytes()[0].is_ascii_alphabetic() || !prefix.ends_with(':') {
        return false;
    }
    let root: Vec<u16> = format!("{prefix}\\")
        .encode_utf16()
        .chain(Some(0))
        .collect();
    // SAFETY: root is an owned, NUL-terminated drive-root string.
    matches!(unsafe { GetDriveTypeW(root.as_ptr()) }, 2 | 3 | 6)
}
