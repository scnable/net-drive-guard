//! 使用系统远程桌面控件承载会话，分辨率更新和连接状态由控件提供。
use std::{cell::RefCell, ffi::c_void, ptr};
use windows::{
    core::{implement, IUnknown, Interface, BSTR, GUID, PCWSTR, VARIANT},
    Win32::System::{
        Com::*,
        Ole::{IOleInPlaceActiveObject, LoadTypeLib, OleInitialize, OleUninitialize},
    },
};
use windows_sys::Win32::{Foundation::*, System::LibraryLoader::*, UI::WindowsAndMessaging::*};

pub const RECONNECT_MESSAGE: u32 = WM_APP + 12;
pub const FULLSCREEN_MESSAGE: u32 = WM_APP + 13;
pub const MODE_MESSAGE: u32 = WM_APP + 16;
pub const SIZE_MESSAGE: u32 = WM_APP + 14;
pub const REMOTE_WIDTH: &str = "NetDriveGuard.RemoteWidth";
pub const REMOTE_HEIGHT: &str = "NetDriveGuard.RemoteHeight";
pub const CLASS: &str = "NetDriveGuardRdpWindow";
pub const STATE: &str = "NetDriveGuard.RdpState";
pub const REASON: &str = "NetDriveGuard.RdpReason";
pub const RESIZE: &str = "NetDriveGuard.RdpResize";
fn trace(stage: &str) {
    if cfg!(debug_assertions) && std::env::var_os("NDG_RDP_TRACE").is_some() {
        eprintln!("rdp_stage:{stage}");
    }
}
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}
fn failure() -> windows::core::Error {
    windows::core::Error::from_hresult(windows::core::HRESULT(0x80004005u32 as i32))
}

type AtlInit = unsafe extern "system" fn() -> i32;
type AtlCreate = unsafe extern "system" fn(
    *const u16,
    HWND,
    *mut c_void,
    *mut *mut c_void,
    *mut *mut c_void,
    *const GUID,
    *mut c_void,
) -> i32;
struct Atl(HMODULE);
impl Drop for Atl {
    fn drop(&mut self) {
        // SAFETY: this owns one successful LoadLibraryExW reference, after all hosted controls were destroyed.
        unsafe {
            FreeLibrary(self.0);
        }
    }
}
fn invoke(
    control: &IDispatch,
    name: &str,
    flags: DISPATCH_FLAGS,
    mut args: Vec<VARIANT>,
) -> windows::core::Result<VARIANT> {
    let name = wide(name);
    let mut id = 0;
    let mut named = -3;
    args.reverse();
    let params = DISPPARAMS {
        rgvarg: args.as_mut_ptr(),
        rgdispidNamedArgs: if flags == DISPATCH_PROPERTYPUT {
            &mut named
        } else {
            ptr::null_mut()
        },
        cArgs: args.len() as u32,
        cNamedArgs: if flags == DISPATCH_PROPERTYPUT { 1 } else { 0 },
    };
    let mut result = VARIANT::default();
    // SAFETY: all argument buffers live through the synchronous COM call.
    unsafe {
        control.GetIDsOfNames(&GUID::zeroed(), &PCWSTR(name.as_ptr()), 1, 0, &mut id)?;
        control.Invoke(
            id,
            &GUID::zeroed(),
            0,
            flags,
            &params,
            Some(&mut result),
            None,
            None,
        )?;
    }
    Ok(result)
}
fn set(control: &IDispatch, name: &str, value: impl Into<VARIANT>) -> windows::core::Result<()> {
    invoke(control, name, DISPATCH_PROPERTYPUT, vec![value.into()]).map(|_| ())
}
fn child_dispatch(control: &IDispatch, name: &str) -> windows::core::Result<IDispatch> {
    let value = invoke(control, name, DISPATCH_PROPERTYGET, vec![])?;
    // SAFETY: inspect the variant tag before borrowing its live COM pointer; clone retains it.
    unsafe {
        let raw = &value.as_raw().Anonymous.Anonymous;
        if raw.vt != 9 {
            return Err(failure());
        }
        let p = raw.Anonymous.pdispVal;
        IDispatch::from_raw_borrowed(&p)
            .cloned()
            .ok_or_else(failure)
    }
}
fn dispatch_value(value: IDispatch) -> VARIANT {
    let mut raw = *VARIANT::default().as_raw();
    // SAFETY: VT_DISPATCH takes ownership of the reference transferred by into_raw.
    unsafe {
        raw.Anonymous.Anonymous.vt = 9;
        raw.Anonymous.Anonymous.Anonymous.pdispVal = value.into_raw();
        VARIANT::from_raw(raw)
    }
}
fn non_scriptable(control: &IDispatch, name: &str, value: bool) -> windows::core::Result<()> {
    let iid = GUID::from_u128(if name == "UseRedirectionServerName" {
        0xfdd029f9_9574_4def_8529_64b521cccaa4
    } else {
        0x4f6996d5_d7b1_412c_b0ff_063718566907
    });
    let path = wide(&format!(
        "{}\\System32\\mstscax.dll",
        std::env::var("SystemRoot").map_err(|_| failure())?
    ));
    let name = wide(name);
    let mut id = 0;
    let mut named = -3;
    let mut arg = VARIANT::from(value);
    let mut result = VARIANT::default();
    let mut raw = ptr::null_mut();
    // SAFETY: QueryInterface supplies the exact interface described by the system type library.
    unsafe {
        control.query(&iid, &mut raw).ok()?;
        let object = IUnknown::from_raw(raw);
        let info = LoadTypeLib(PCWSTR(path.as_ptr()))?.GetTypeInfoOfGuid(&iid)?;
        info.GetIDsOfNames(&PCWSTR(name.as_ptr()), 1, &mut id)?;
        let mut params = DISPPARAMS {
            rgvarg: &mut arg,
            rgdispidNamedArgs: &mut named,
            cArgs: 1,
            cNamedArgs: 1,
        };
        info.Invoke(
            object.as_raw(),
            id,
            DISPATCH_PROPERTYPUT,
            &mut params,
            &mut result,
            ptr::null_mut(),
            ptr::null_mut(),
        )?;
    }
    Ok(())
}
#[implement(IDispatch)]
struct EventSink {
    window: usize,
    status: usize,
}
#[allow(non_snake_case)]
impl IDispatch_Impl for EventSink_Impl {
    fn GetTypeInfoCount(&self) -> windows::core::Result<u32> {
        Ok(0)
    }
    fn GetTypeInfo(&self, _: u32, _: u32) -> windows::core::Result<ITypeInfo> {
        Err(failure())
    }
    fn GetIDsOfNames(
        &self,
        _: *const GUID,
        _: *const PCWSTR,
        _: u32,
        _: u32,
        _: *mut i32,
    ) -> windows::core::Result<()> {
        Err(failure())
    }
    fn Invoke(
        &self,
        _: i32,
        _: *const GUID,
        _: u32,
        _: DISPATCH_FLAGS,
        params: *const DISPPARAMS,
        _: *mut VARIANT,
        _: *mut EXCEPINFO,
        _: *mut u32,
    ) -> windows::core::Result<()> {
        // SAFETY: callback runs on the owning STA while its window exists; COM owns arguments for this call.
        unsafe {
            if self.status == 9 {
                PostMessageW(self.window as HWND, SIZE_MESSAGE, 0, 0);
                return Ok(());
            }
            if self.status == 7 || self.status == 8 {
                let enter = self.status == 7;
                trace(if enter {
                    "fullscreen_enter_event"
                } else {
                    "fullscreen_leave_event"
                });
                let expected = CLIENT.with(|c| {
                    c.borrow_mut()
                        .as_mut()
                        .and_then(|c| c.fullscreen_request.take())
                });
                if expected == Some(enter) {
                    return Ok(());
                }
                PostMessageW(
                    self.window as HWND,
                    FULLSCREEN_MESSAGE,
                    usize::from(self.status == 7),
                    0,
                );
                return Ok(());
            }
            SetPropW(
                self.window as HWND,
                wide(STATE).as_ptr(),
                self.status as HANDLE,
            );
            if self.status == 4 || self.status == 6 {
                let reason =
                    if !params.is_null() && (*params).cArgs > 0 && !(*params).rgvarg.is_null() {
                        i32::try_from(&*(*params).rgvarg).unwrap_or(0)
                    } else {
                        0
                    };
                SetPropW(
                    self.window as HWND,
                    wide(REASON).as_ptr(),
                    reason as usize as HANDLE,
                );
            }
        }
        Ok(())
    }
}
struct Client {
    control: IDispatch,
    child: HWND,
    size: (i32, i32),
    pending: (i32, i32),
    ticks: u8,
    attempts: u8,
    fullscreen_request: Option<bool>,
}
thread_local! { static CLIENT: RefCell<Option<Client>> = const { RefCell::new(None) }; }
unsafe extern "system" fn procedure(window: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    // SAFETY: Windows calls this procedure for our registered class with valid message parameters.
    unsafe {
        match message {
            MODE_MESSAGE => {
                let control = CLIENT.with(|c| c.borrow().as_ref().map(|c| c.control.clone()));
                if let Some(control) = control {
                    let desired = w != 0;
                    if invoke(&control, "FullScreen", DISPATCH_PROPERTYGET, vec![])
                        .and_then(|v| bool::try_from(&v))
                        .ok()
                        != Some(desired)
                    {
                        CLIENT.with(|c| {
                            if let Some(c) = c.borrow_mut().as_mut() {
                                c.fullscreen_request = Some(desired);
                            }
                        });
                        if let Err(error) = set(&control, "FullScreen", desired) {
                            trace(&format!("fullscreen_hresult:{:08x}", error.code().0));
                            CLIENT.with(|c| {
                                if let Some(c) = c.borrow_mut().as_mut() {
                                    c.fullscreen_request = None;
                                }
                            });
                        }
                    }
                }
                0
            }
            SIZE_MESSAGE => {
                let control = CLIENT.with(|c| c.borrow().as_ref().map(|v| v.control.clone()));
                if let Some(control) = control {
                    for (property, key) in [
                        ("DesktopWidth", REMOTE_WIDTH),
                        ("DesktopHeight", REMOTE_HEIGHT),
                    ] {
                        if let Ok(value) = invoke(&control, property, DISPATCH_PROPERTYGET, vec![])
                            .and_then(|v| i32::try_from(&v))
                        {
                            SetPropW(window, wide(key).as_ptr(), value as usize as HANDLE);
                        }
                    }
                }
                0
            }
            RECONNECT_MESSAGE => {
                let control = CLIENT.with(|c| c.borrow().as_ref().map(|v| v.control.clone()));
                if let Some(control) = control {
                    if matches!(GetPropW(window, wide(STATE).as_ptr()) as usize, 4 | 6) {
                        let _ = invoke(&control, "Connect", DISPATCH_METHOD, vec![]);
                    }
                }
                0
            }
            FULLSCREEN_MESSAGE => {
                let m = windows_sys::Win32::Graphics::Gdi::MonitorFromWindow(
                    window,
                    windows_sys::Win32::Graphics::Gdi::MONITOR_DEFAULTTONEAREST,
                );
                let mut info = windows_sys::Win32::Graphics::Gdi::MONITORINFO {
                    cbSize: std::mem::size_of::<windows_sys::Win32::Graphics::Gdi::MONITORINFO>()
                        as u32,
                    ..Default::default()
                };
                if windows_sys::Win32::Graphics::Gdi::GetMonitorInfoW(m, &mut info) != 0 {
                    let r = if w != 0 { info.rcMonitor } else { info.rcWork };
                    SetWindowLongPtrW(
                        window,
                        GWL_STYLE,
                        if w != 0 {
                            WS_POPUP | WS_VISIBLE
                        } else {
                            WS_OVERLAPPEDWINDOW | WS_VISIBLE
                        } as isize,
                    );
                    SetWindowPos(
                        window,
                        ptr::null_mut(),
                        r.left,
                        r.top,
                        r.right - r.left,
                        r.bottom - r.top,
                        SWP_NOZORDER | SWP_FRAMECHANGED,
                    );
                }
                0
            }
            WM_SIZE => {
                let width = (l as u32 & 0xffff) as i32;
                let height = ((l as u32 >> 16) & 0xffff) as i32;
                if width > 0 && height > 0 {
                    let child = CLIENT.with(|c| {
                        let mut c = c.borrow_mut();
                        let c = c.as_mut()?;
                        c.pending = (width, height);
                        c.ticks = 0;
                        c.attempts = 0;
                        Some(c.child)
                    });
                    if let Some(child) = child {
                        SetWindowPos(
                            child,
                            ptr::null_mut(),
                            0,
                            0,
                            width,
                            height,
                            SWP_NOZORDER | SWP_NOACTIVATE,
                        );
                    }
                }
                0
            }
            WM_TIMER => {
                // Clone the interface before invoking COM: callbacks can synchronously dispatch window messages.
                let update = CLIENT.with(|c| {
                    let mut c = c.borrow_mut();
                    let c = c.as_mut()?;
                    c.ticks = c.ticks.saturating_add(1);
                    if c.ticks < 3
                        || c.pending == c.size
                        || c.attempts >= 20
                        || !matches!(GetPropW(window, wide(STATE).as_ptr()) as usize, 2 | 3)
                    {
                        return None;
                    }
                    c.ticks = 0;
                    c.attempts = c.attempts.saturating_add(1);
                    Some((c.control.clone(), c.pending))
                });
                if let Some((control, (width, height))) = update {
                    let result = invoke(
                        &control,
                        "UpdateSessionDisplaySettings",
                        DISPATCH_METHOD,
                        vec![
                            (width as u32).into(),
                            (height as u32).into(),
                            0u32.into(),
                            0u32.into(),
                            0u32.into(),
                            100u32.into(),
                            100u32.into(),
                        ],
                    );
                    if result.is_ok() {
                        CLIENT.with(|c| {
                            if let Some(c) = c.borrow_mut().as_mut() {
                                c.size = (width, height);
                            }
                        });
                    } else if let Err(error) = &result {
                        trace(&format!("resize_hresult:{:08x}", error.code().0));
                    }
                    SetPropW(
                        window,
                        wide(RESIZE).as_ptr(),
                        if result.is_ok() { 1usize } else { 2usize } as HANDLE,
                    );
                }
                0
            }
            WM_SETFOCUS => {
                let child = CLIENT.with(|c| c.borrow().as_ref().map(|c| c.child));
                if let Some(child) = child {
                    windows_sys::Win32::UI::Input::KeyboardAndMouse::SetFocus(child);
                }
                0
            }
            WM_CLOSE => {
                PostQuitMessage(0);
                0
            }
            WM_DESTROY => {
                KillTimer(window, 1);
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(window, message, w, l),
        }
    }
}
/// 独立进程运行，不随主窗口关闭而结束；参数不包含凭据。
pub fn run(
    host: &str,
    port: u16,
    width: i32,
    height: i32,
    name: &str,
    probe: bool,
) -> Result<(), String> {
    if host.is_empty()
        || host.chars().any(char::is_control)
        || port == 0
        || !(200..=16384).contains(&width)
        || !(200..=16384).contains(&height)
    {
        return Err("host_settings_invalid".into());
    }
    // SAFETY: set DPI awareness before creating windows; an existing manifest setting may already have done so.
    unsafe {
        windows_sys::Win32::UI::HiDpi::SetProcessDpiAwarenessContext(
            windows_sys::Win32::UI::HiDpi::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
        );
    }
    // SAFETY: this process starts one STA, and balances initialization after all controls are released.
    unsafe {
        OleInitialize(None).map_err(|e| format!("rdp_start_failed: {e}"))?;
    }
    let result = run_sta(host, port, width, height, name, probe);
    trace("uninitialize");
    // SAFETY: run_sta has released every COM object on this same thread.
    unsafe {
        OleUninitialize();
    }
    trace("finished");
    result.map_err(|e| format!("rdp_start_failed: {e}"))
}
fn run_sta(
    host: &str,
    port: u16,
    width: i32,
    height: i32,
    name: &str,
    probe: bool,
) -> windows::core::Result<()> {
    let class = wide(CLASS);
    let title = wide(name);
    let mut control_raw = ptr::null_mut();
    let mut host_raw = ptr::null_mut();
    // SAFETY: class and title buffers remain valid during creation; all resources are local to this STA.
    unsafe {
        let library = Atl(LoadLibraryExW(
            wide("atl.dll").as_ptr(),
            ptr::null_mut(),
            LOAD_LIBRARY_SEARCH_SYSTEM32,
        ));
        if library.0.is_null() {
            return Err(failure());
        }
        let init: AtlInit = std::mem::transmute(
            GetProcAddress(library.0, c"AtlAxWinInit".as_ptr().cast()).ok_or_else(failure)?,
        );
        let create: AtlCreate = std::mem::transmute(
            GetProcAddress(library.0, c"AtlAxCreateControlEx".as_ptr().cast())
                .ok_or_else(failure)?,
        );
        if init() == 0 {
            return Err(failure());
        }
        let instance = GetModuleHandleW(ptr::null());
        let wc = WNDCLASSW {
            lpfnWndProc: Some(procedure),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            hCursor: LoadCursorW(ptr::null_mut(), IDC_ARROW),
            ..Default::default()
        };
        if RegisterClassW(&wc) == 0 {
            return Err(failure());
        }
        let window = CreateWindowExW(
            0,
            class.as_ptr(),
            title.as_ptr(),
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            width,
            height,
            ptr::null_mut(),
            ptr::null_mut(),
            instance,
            ptr::null(),
        );
        if window.is_null() {
            return Err(failure());
        }
        let result = (|| {
            let child = CreateWindowExW(
                0,
                wide("Static").as_ptr(),
                ptr::null(),
                WS_CHILD | WS_VISIBLE,
                0,
                0,
                width,
                height,
                window,
                ptr::null_mut(),
                instance,
                ptr::null(),
            );
            if child.is_null() {
                return Err(failure());
            }
            windows::core::HRESULT(create(
                wide("{A0C63C30-F08D-4AB4-907C-34905D770C7D}").as_ptr(),
                child,
                ptr::null_mut(),
                &mut host_raw,
                &mut control_raw,
                &GUID::zeroed(),
                ptr::null_mut(),
            ))
            .ok()?;
            let container = IUnknown::from_raw(host_raw);
            host_raw = ptr::null_mut();
            let unknown = IUnknown::from_raw(control_raw);
            control_raw = ptr::null_mut();
            let control: IDispatch = unknown.cast()?;
            set(&control, "Server", BSTR::from(host))?;
            set(&control, "DesktopWidth", width)?;
            set(&control, "DesktopHeight", height)?;
            set(&control, "ColorDepth", 32i32)?;
            let advanced = child_dispatch(&control, "AdvancedSettings9")?;
            set(&advanced, "RDPPort", port as i32)?;
            set(&advanced, "EnableCredSspSupport", true)?;
            set(&advanced, "SmartSizing", true)?;
            set(&advanced, "RedirectClipboard", true)?;
            set(&advanced, "EnableAutoReconnect", true)?;
            set(&advanced, "ContainerHandledFullScreen", 1i32)?;
            let secured = child_dispatch(&control, "SecuredSettings2")?;
            set(&secured, "KeyboardHookMode", 0i32)?;
            non_scriptable(&control, "AllowPromptingForCredentials", true)?;
            non_scriptable(&control, "PromptForCredentials", true)?;
            non_scriptable(&control, "UseRedirectionServerName", true)?;
            let mut sinks = Vec::new();
            for (event, status) in [
                ("OnConnecting", 1),
                ("OnConnected", 2),
                ("OnLoginComplete", 3),
                ("OnDisconnected", 4),
                ("OnAutoReconnecting", 5),
                ("OnAutoReconnected", 3),
                ("OnFatalError", 6),
                ("OnRemoteDesktopSizeChange", 9),
                ("OnRequestGoFullScreen", 7),
                ("OnRequestLeaveFullScreen", 8),
            ] {
                let sink: IDispatch = EventSink {
                    window: window as usize,
                    status,
                }
                .into();
                invoke(
                    &control,
                    "attachEvent",
                    DISPATCH_METHOD,
                    vec![BSTR::from(event).into(), dispatch_value(sink.clone())],
                )?;
                sinks.push((event, sink));
            }
            if !probe {
                CLIENT.with(|c| {
                    *c.borrow_mut() = Some(Client {
                        control: control.clone(),
                        child,
                        size: (width, height),
                        pending: (width, height),
                        ticks: 0,
                        attempts: 0,
                        fullscreen_request: None,
                    })
                });
                SetTimer(window, 1, 150, None);
                ShowWindow(window, SW_SHOW);
                // 首次 ShowWindow 可能受父进程的隐藏启动标记覆盖；远程窗口应当独立可见。
                ShowWindow(window, SW_SHOW);
                invoke(&control, "Connect", DISPATCH_METHOD, vec![])?;
                let active: IOleInPlaceActiveObject = control.cast()?;
                let mut message = MSG::default();
                while GetMessageW(&mut message, ptr::null_mut(), 0, 0) > 0 {
                    // windows 和 windows-sys 的 MSG 来自同一 Win32 ABI。
                    if (WM_KEYFIRST..=WM_KEYLAST).contains(&message.message)
                        && (active.vtable().TranslateAccelerator)(
                            active.as_raw(),
                            (&message as *const MSG).cast(),
                        )
                        .0 == 0
                    {
                        continue;
                    }
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            }
            if !probe {
                trace("disconnect");
                let _ = invoke(&control, "Disconnect", DISPATCH_METHOD, vec![]);
            }
            trace("detach_events");
            for (event, sink) in sinks {
                let _ = invoke(
                    &control,
                    "detachEvent",
                    DISPATCH_METHOD,
                    vec![BSTR::from(event).into(), dispatch_value(sink)],
                );
            }
            CLIENT.with(|c| {
                c.borrow_mut().take();
            });
            trace("release_container");
            drop(container);
            Ok(())
        })();
        CLIENT.with(|c| {
            c.borrow_mut().take();
        });
        if !control_raw.is_null() {
            drop(IUnknown::from_raw(control_raw));
        }
        if !host_raw.is_null() {
            drop(IUnknown::from_raw(host_raw));
        }
        trace("destroy_window");
        if IsWindow(window) != 0 {
            DestroyWindow(window);
        }
        trace("window_destroyed");
        UnregisterClassW(class.as_ptr(), instance);
        result
    }
}
