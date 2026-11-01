//! Windows callbacks only set atomic flags. They never call UI or wait on locks.
use std::{
    ffi::c_void,
    io, ptr,
    sync::atomic::{AtomicU8, Ordering},
};
use windows_sys::Win32::{
    Foundation::HANDLE,
    NetworkManagement::IpHelper::{
        CancelMibChangeNotify2, NotifyIpInterfaceChange, MIB_IPINTERFACE_ROW, MIB_NOTIFICATION_TYPE,
    },
    Networking::WinSock::AF_UNSPEC,
    System::Power::{
        PowerRegisterSuspendResumeNotification, PowerUnregisterSuspendResumeNotification,
        DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS,
    },
    UI::WindowsAndMessaging::DEVICE_NOTIFY_CALLBACK,
};
static CHANGED: AtomicU8 = AtomicU8::new(0);
unsafe extern "system" fn network(
    _: *const c_void,
    _: *const MIB_IPINTERFACE_ROW,
    _: MIB_NOTIFICATION_TYPE,
) {
    CHANGED.fetch_or(1, Ordering::Release);
}
unsafe extern "system" fn power(_: *const c_void, kind: u32, _: *const c_void) -> u32 {
    if kind == 7 || kind == 18 {
        CHANGED.fetch_or(2, Ordering::Release);
    }
    0
}
pub struct Signals {
    network: HANDLE,
    power: *mut c_void,
    _subscription: Box<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>,
}
impl Signals {
    /// Construct and drop on the owning monitor thread. Registration cleanup
    /// waits for OS callbacks; callbacks only touch process-lifetime statics.
    pub fn new() -> io::Result<Self> {
        let mut value = Self {
            network: ptr::null_mut(),
            power: ptr::null_mut(),
            _subscription: Box::new(DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS {
                Callback: Some(power),
                Context: ptr::null_mut(),
            }),
        };
        // SAFETY: callbacks are static functions, context is unused, output lives through call.
        let code = unsafe {
            NotifyIpInterfaceChange(
                AF_UNSPEC,
                Some(network),
                ptr::null(),
                false,
                &mut value.network,
            )
        };
        if code != 0 {
            return Err(io::Error::from_raw_os_error(code as i32));
        }
        // SAFETY: the subscription is heap allocated until unregister; callback and context
        // have static lifetime. Successful registration is unregistered in Drop.
        let code = unsafe {
            PowerRegisterSuspendResumeNotification(
                DEVICE_NOTIFY_CALLBACK,
                (&mut *value._subscription as *mut DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS).cast(),
                &mut value.power,
            )
        };
        if code != 0 {
            return Err(io::Error::from_raw_os_error(code as i32));
        }
        Ok(value)
    }
    pub fn take(&self) -> u8 {
        CHANGED.swap(0, Ordering::AcqRel)
    }
}
impl Drop for Signals {
    fn drop(&mut self) {
        // SAFETY: handles came from successful registrations and are owned here.
        unsafe {
            if !self.network.is_null() {
                CancelMibChangeNotify2(self.network);
            }
            if !self.power.is_null() {
                PowerUnregisterSuspendResumeNotification(self.power as isize);
            }
        }
    }
}
