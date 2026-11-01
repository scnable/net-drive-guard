//! The credential allocation and every owned password buffer are wiped before release.
use net_drive_guard_core::config::Config;
use sha2::{Digest, Sha256};
use std::{io, ptr, slice};
use windows_sys::Win32::{
    Foundation::GetLastError,
    Globalization::{LCMapStringEx, LCMAP_UPPERCASE},
    Security::Credentials::*,
};
use zeroize::Zeroize;

pub struct Secret {
    pub(crate) user: Vec<u16>,
    pub(crate) password: Vec<u16>,
}
impl Drop for Secret {
    fn drop(&mut self) {
        self.user.zeroize();
        self.password.zeroize();
    }
}
fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

/// Retains the C# target format; NLS invariant uppercase avoids Rust's expanding
/// Unicode case conversion changing the credential key for an existing share.
pub fn target(config: &Config) -> io::Result<String> {
    let source: Vec<u16> = config.share().encode_utf16().collect();
    let mut upper = vec![0; source.len() * 3 + 1];
    // SAFETY: buffers are valid and lengths are bounded by owned allocations.
    let n = unsafe {
        LCMapStringEx(
            [0u16].as_ptr(),
            LCMAP_UPPERCASE,
            source.as_ptr(),
            source.len() as i32,
            upper.as_mut_ptr(),
            upper.len() as i32,
            ptr::null(),
            ptr::null(),
            0,
        )
    };
    if n == 0 {
        return Err(io::Error::last_os_error());
    }
    let canonical =
        String::from_utf16(&upper[..n as usize]).map_err(|_| io::Error::from_raw_os_error(13))?;
    let hash = Sha256::digest(canonical.as_bytes());
    let suffix: String = hash[..16].iter().map(|v| format!("{v:02X}")).collect();
    Ok(format!(
        "NetDriveGuard:Mapping:{}:{suffix}",
        &config.drive()[..1]
    ))
}
struct Allocation(*mut CREDENTIALW);
impl Drop for Allocation {
    fn drop(&mut self) {
        // SAFETY: CredRead allocated this block. Its validated blob is writable
        // until CredFree; no other owner survives this wrapper.
        unsafe {
            let c = &mut *self.0;
            if !c.CredentialBlob.is_null() && c.CredentialBlobSize <= 2560 {
                slice::from_raw_parts_mut(c.CredentialBlob, c.CredentialBlobSize as usize)
                    .zeroize();
            }
            CredFree(self.0.cast());
        }
    }
}
pub fn read(config: &Config) -> io::Result<Option<Secret>> {
    let name = wide(&target(config)?);
    let mut raw = ptr::null_mut();
    // SAFETY: name is NUL terminated; raw receives the API-owned allocation.
    if unsafe { CredReadW(name.as_ptr(), CRED_TYPE_GENERIC, 0, &mut raw) } == 0 {
        // SAFETY: GetLastError has no preconditions and immediately follows failure.
        let code = unsafe { GetLastError() };
        return if code == 1168 {
            Ok(None)
        } else {
            Err(io::Error::from_raw_os_error(code as i32))
        };
    }
    if raw.is_null() {
        return Err(io::Error::from_raw_os_error(13));
    }
    let allocation = Allocation(raw);
    // SAFETY: successful CredRead supplies a live CREDENTIALW allocation.
    let c = unsafe { &*allocation.0 };
    if c.CredentialBlobSize > 2560
        || c.CredentialBlobSize % 2 != 0
        || (c.CredentialBlobSize != 0 && c.CredentialBlob.is_null())
        || c.UserName.is_null()
    {
        return Err(io::Error::from_raw_os_error(13));
    }
    let mut user = Vec::new();
    for i in 0..=513 {
        // SAFETY: WinCred's documented UserName is NUL terminated (max 513 units).
        let ch = unsafe { *c.UserName.add(i) };
        user.push(ch);
        if ch == 0 {
            break;
        }
    }
    if user.last() != Some(&0) {
        return Err(io::Error::from_raw_os_error(13));
    }
    // SAFETY: blob is a live API buffer of the validated byte length. Read bytes
    // rather than casting to u16, because the API does not promise alignment.
    let bytes = if c.CredentialBlobSize == 0 {
        &[]
    } else {
        // SAFETY: nonzero blob length and its non-null pointer were validated above.
        unsafe { slice::from_raw_parts(c.CredentialBlob, c.CredentialBlobSize as usize) }
    };
    let mut password: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|b| u16::from_le_bytes([b[0], b[1]]))
        .collect();
    password.push(0);
    Ok(Some(Secret { user, password }))
}
pub fn write(config: &Config, user: &str, password: &str) -> io::Result<()> {
    if user.trim().is_empty()
        || user.encode_utf16().count() > 513
        || user.contains('\0')
        || password.contains('\0')
        || password.encode_utf16().count() > 1280
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "credential_invalid",
        ));
    }
    let mut secret = Secret {
        user: wide(user),
        password: wide(password),
    };
    let mut name = wide(&target(config)?);
    let credential = CREDENTIALW {
        Type: CRED_TYPE_GENERIC,
        TargetName: name.as_mut_ptr(),
        UserName: secret.user.as_mut_ptr(),
        CredentialBlob: secret.password.as_mut_ptr().cast(),
        CredentialBlobSize: ((secret.password.len() - 1) * 2) as u32,
        Persist: CRED_PERSIST_LOCAL_MACHINE,
        ..Default::default()
    };
    // SAFETY: all pointers remain live throughout the synchronous call; the blob
    // excludes its trailing NUL and will be wiped by Secret::drop on every path.
    if unsafe { CredWriteW(&credential, 0) } == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}
pub fn delete(config: &Config) -> io::Result<()> {
    let name = wide(&target(config)?);
    // SAFETY: name is valid and NUL terminated; this targets only this mapping.
    if unsafe { CredDeleteW(name.as_ptr(), CRED_TYPE_GENERIC, 0) } != 0 {
        return Ok(());
    }
    let error = io::Error::last_os_error();
    if error.raw_os_error() == Some(1168) {
        Ok(())
    } else {
        Err(error)
    }
}
