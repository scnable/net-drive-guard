//! 只管理本程序启动且进程身份仍匹配的远程窗口，不结束远程会话。
use serde::{Deserialize, Serialize};
use std::{
    path::Path,
    process::Command,
    ptr, thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{CloseHandle, FILETIME, HWND, LPARAM, RECT},
    Graphics::Gdi::{
        EnumDisplayDevicesW, EnumDisplayMonitors, GetMonitorInfoW, MonitorFromWindow,
        DISPLAY_DEVICEW, HDC, HMONITOR, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
    },
    System::Threading::{
        GetProcessTimes, OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
    },
    UI::{Input::KeyboardAndMouse::IsWindowEnabled, WindowsAndMessaging::*},
};
fn text(s: &[u16]) -> String {
    String::from_utf16_lossy(&s[..s.iter().position(|c| *c == 0).unwrap_or(s.len())])
}
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Monitor {
    pub id: String,
    pub name: String,
    pub primary: bool,
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub work_x: i32,
    pub work_y: i32,
    pub work_width: i32,
    pub work_height: i32,
}
unsafe extern "system" fn collect_monitor(h: HMONITOR, _: HDC, _: *mut RECT, data: LPARAM) -> i32 {
    let mut info = MONITORINFOEXW::default();
    info.monitorInfo.cbSize = std::mem::size_of_val(&info) as u32;
    // SAFETY: API receives a correctly sized output structure.
    if unsafe { GetMonitorInfoW(h, (&mut info as *mut MONITORINFOEXW).cast()) } == 0 {
        return 1;
    }
    let mut device = DISPLAY_DEVICEW::default();
    device.cb = std::mem::size_of_val(&device) as u32;
    // SAFETY: the monitor device name is terminated and the output has its size set.
    let found = unsafe { EnumDisplayDevicesW(info.szDevice.as_ptr(), 0, &mut device, 1) } != 0;
    let bounds = info.monitorInfo.rcMonitor;
    let work = info.monitorInfo.rcWork;
    let id = if found && !text(&device.DeviceID).is_empty() {
        text(&device.DeviceID)
    } else {
        text(&info.szDevice)
    };
    // SAFETY: EnumDisplayMonitors invokes synchronously with our Vec pointer.
    unsafe { &mut *(data as *mut Vec<Monitor>) }.push(Monitor {
        id,
        name: if found {
            text(&device.DeviceString)
        } else {
            text(&info.szDevice)
        },
        primary: info.monitorInfo.dwFlags & 1 != 0,
        x: bounds.left,
        y: bounds.top,
        width: bounds.right - bounds.left,
        height: bounds.bottom - bounds.top,
        work_x: work.left,
        work_y: work.top,
        work_width: work.right - work.left,
        work_height: work.bottom - work.top,
    });
    1
}
pub fn monitors() -> Vec<Monitor> {
    let mut list = Vec::new();
    // SAFETY: callback only uses the provided live Vec during this call.
    unsafe {
        EnumDisplayMonitors(
            ptr::null_mut(),
            ptr::null(),
            Some(collect_monitor),
            (&mut list as *mut Vec<Monitor>) as isize,
        );
    }
    list
}
fn process_identity(pid: u32) -> Option<(u64, String)> {
    // SAFETY: read-only process handle; every success is closed below.
    let h = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if h.is_null() {
        return None;
    }
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    let mut buffer = vec![0u16; 32768];
    let mut n = buffer.len() as u32;
    // SAFETY: all outputs are live and correctly sized; h is valid.
    let ok = unsafe {
        GetProcessTimes(h, &mut created, &mut exited, &mut kernel, &mut user) != 0
            && QueryFullProcessImageNameW(h, 0, buffer.as_mut_ptr(), &mut n) != 0
    };
    // SAFETY: we own the handle returned by OpenProcess.
    unsafe {
        CloseHandle(h);
    }
    if !ok {
        return None;
    }
    Some((
        ((created.dwHighDateTime as u64) << 32) | created.dwLowDateTime as u64,
        String::from_utf16_lossy(&buffer[..n as usize]),
    ))
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct RemoteWindow {
    pub pid: u32,
    pub created: u64,
    pub path: String,
    pub hwnd: usize,
    pub endpoint: String,
}
impl RemoteWindow {
    pub fn refresh(&mut self) -> bool {
        let Some((created, path)) = process_identity(self.pid) else {
            return false;
        };
        if created != self.created || !path.eq_ignore_ascii_case(&self.path) {
            return false;
        }
        let mut pair = (self.pid, 0usize);
        // SAFETY: pair lives throughout the synchronous window enumeration.
        unsafe {
            EnumWindows(Some(find_window), (&mut pair as *mut (u32, usize)) as isize);
        }
        self.hwnd = pair.1;
        true
    }
    pub fn native(&self) -> bool {
        window_class(self.hwnd as HWND) == crate::rdp_client::CLASS
    }
    pub fn connection_state(&self) -> &'static str {
        if !self.valid() {
            return "closed";
        }
        if !self.native() {
            return "window_open";
        }
        let name: Vec<u16> = crate::rdp_client::STATE
            .encode_utf16()
            .chain(Some(0))
            .collect();
        // SAFETY: verified owned window; property contains a small state number, never a dereferenced pointer.
        match unsafe { GetPropW(self.hwnd as HWND, name.as_ptr()) as usize } {
            2 | 3 => "connected",
            4 => "disconnected",
            5 => "reconnecting",
            6 => "connection_failed",
            _ => "waiting",
        }
    }
    pub fn connection_code(&self) -> Option<u32> {
        if !self.native()
            || !matches!(
                self.connection_state(),
                "disconnected" | "connection_failed"
            )
        {
            return None;
        }
        let name: Vec<u16> = crate::rdp_client::REASON
            .encode_utf16()
            .chain(Some(0))
            .collect();
        // SAFETY: the validated owned window stores the numeric protocol reason as a property.
        let code = unsafe { GetPropW(self.hwnd as HWND, name.as_ptr()) as usize as u32 };
        (code != 0).then_some(code)
    }
    pub fn reconnect(&self) -> Result<(), String> {
        if !self.valid() {
            return Err("rdp_window_closed".into());
        }
        if self.native()
            && matches!(
                self.connection_state(),
                "disconnected" | "connection_failed"
            )
        {
            // SAFETY: only our own validated window accepts this application-specific message.
            if unsafe {
                PostMessageW(
                    self.hwnd as HWND,
                    crate::rdp_client::RECONNECT_MESSAGE,
                    0,
                    0,
                )
            } == 0
            {
                return Err("rdp_start_failed".into());
            }
        }
        Ok(())
    }
    pub fn ready(&self) -> bool {
        if self.native() && self.connection_state() != "connected" {
            return false;
        }
        if !self.valid() {
            return false;
        }
        // SAFETY: 窗口身份已验证；仅查询可见、启用和弹窗状态，无指针解引用。
        unsafe {
            let h = self.hwnd as HWND;
            let popup = GetLastActivePopup(h);
            IsWindowVisible(h) != 0
                && IsWindowEnabled(h) != 0
                && (popup == h || IsWindowVisible(popup) == 0)
        }
    }
    pub fn valid(&self) -> bool {
        let Some((created, path)) = process_identity(self.pid) else {
            return false;
        };
        if created != self.created || !path.eq_ignore_ascii_case(&self.path) {
            return false;
        }
        let mut pid = 0;
        // SAFETY: Windows validates possibly expired window handles.
        unsafe {
            GetWindowThreadProcessId(self.hwnd as HWND, &mut pid);
        }
        pid == self.pid
            && matches!(
                window_class(self.hwnd as HWND).as_str(),
                "TscShellContainerClass" | crate::rdp_client::CLASS
            )
    }
}
fn window_class(h: HWND) -> String {
    let mut b = [0u16; 256];
    // SAFETY: buffer has 256 writable UTF-16 units.
    let n = unsafe { GetClassNameW(h, b.as_mut_ptr(), b.len() as i32) };
    text(&b[..n.max(0) as usize])
}
unsafe extern "system" fn find_window(h: HWND, data: LPARAM) -> i32 {
    // SAFETY: pointer is a live search pair for the synchronous enumeration.
    let pair = unsafe { &mut *(data as *mut (u32, usize)) };
    let mut pid = 0;
    // SAFETY: h is supplied by EnumWindows; pid is writable.
    unsafe {
        GetWindowThreadProcessId(h, &mut pid);
    }
    if pid == pair.0
        && matches!(
            window_class(h).as_str(),
            "TscShellContainerClass" | crate::rdp_client::CLASS
        )
    {
        pair.1 = h as usize;
        return 0;
    }
    1
}
pub fn launch(
    endpoint: &str,
    monitor: &Monitor,
    display: &str,
    rdp_file: &Path,
) -> Result<RemoteWindow, String> {
    if endpoint.chars().any(char::is_control) {
        return Err("host_address_invalid".into());
    }
    let body=format!("full address:s:{endpoint}\r\nkeyboardhook:i:0\r\nuse redirection server name:i:1\r\nuse multimon:i:0\r\nscreen mode id:i:{}\r\ndesktopwidth:i:{}\r\ndesktopheight:i:{}\r\nwinposstr:s:0,1,{},{},{},{}\r\nsmart sizing:i:1\r\nprompt for credentials:i:1\r\nredirectclipboard:i:1\r\n",if display=="fullscreen"{2}else{1},monitor.width,monitor.height,monitor.x,monitor.y,monitor.x+monitor.width,monitor.y+monitor.height);
    std::fs::write(rdp_file, body).map_err(|_| "rdp_file_failed")?;
    let exe = std::env::var_os("SystemRoot").ok_or("rdp_start_failed")?;
    let child = Command::new(Path::new(&exe).join("System32/mstsc.exe"))
        .arg(rdp_file)
        .spawn()
        .map_err(|_| "rdp_start_failed")?;
    let pid = child.id();
    let (created, path) = process_identity(pid).ok_or("rdp_start_failed")?;
    // 不加入检查任务的 Job Object；丢弃 Child 不会结束 mstsc。
    drop(child);
    let start = Instant::now();
    let mut pair = (pid, 0usize);
    while start.elapsed() < Duration::from_secs(12) {
        // SAFETY: pair lives throughout synchronous enumeration.
        unsafe {
            EnumWindows(Some(find_window), (&mut pair as *mut (u32, usize)) as isize);
        }
        if pair.1 != 0 {
            return Ok(RemoteWindow {
                pid,
                created,
                path,
                hwnd: pair.1,
                endpoint: endpoint.into(),
            });
        }
        thread::sleep(Duration::from_millis(100));
    }
    Ok(RemoteWindow {
        pid,
        created,
        path,
        hwnd: 0,
        endpoint: endpoint.into(),
    })
}
pub fn launch_native(
    host: &str,
    port: u16,
    name: &str,
    monitor: &Monitor,
) -> Result<RemoteWindow, String> {
    let exe = std::env::current_exe().map_err(|_| "rdp_start_failed")?;
    let mut child = Command::new(exe)
        .arg("--ndg-rdp")
        .arg(host)
        .arg(port.to_string())
        .arg(monitor.width.to_string())
        .arg(monitor.height.to_string())
        .arg(name)
        .spawn()
        .map_err(|_| "rdp_start_failed")?;
    let pid = child.id();
    let (created, path) = process_identity(pid).ok_or("rdp_start_failed")?;
    let endpoint = if host.contains(':') {
        format!("[{host}]:{port}")
    } else {
        format!("{host}:{port}")
    };
    let mut window = RemoteWindow {
        pid,
        created,
        path,
        hwnd: 0,
        endpoint,
    };
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(12) {
        if child.try_wait().map_err(|_| "rdp_start_failed")?.is_some() {
            return Err("rdp_start_failed".into());
        }
        window.refresh();
        if window.hwnd != 0 {
            return Ok(window);
        }
        thread::sleep(Duration::from_millis(50));
    }
    Ok(window)
}
pub fn choose_monitor(id: &str) -> Result<(Monitor, bool), String> {
    let list = monitors();
    if let Some(m) = list.iter().find(|m| {
        if id == "primary" {
            m.primary
        } else {
            m.id == id
        }
    }) {
        return Ok((m.clone(), false));
    }
    list.into_iter()
        .find(|m| m.primary)
        .map(|m| (m, true))
        .ok_or("monitor_unavailable".into())
}
pub fn foreground() -> usize {
    // SAFETY: no preconditions; handle is checked before subsequent operations.
    unsafe { GetForegroundWindow() as usize }
}
pub fn foreground_monitor() -> Option<Monitor> {
    // SAFETY: the foreground window and nearest monitor are queried read-only.
    let h = unsafe { MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST) };
    let mut list = Vec::new();
    // SAFETY: callback receives a live Vec and only reads the valid monitor.
    unsafe {
        collect_monitor(
            h,
            ptr::null_mut(),
            ptr::null_mut(),
            (&mut list as *mut Vec<Monitor>) as isize,
        );
    }
    list.into_iter().next()
}
pub fn place(
    window: &RemoteWindow,
    monitor: &Monitor,
    display: &str,
    activate: bool,
) -> Result<(), String> {
    if !window.valid() {
        return Err("rdp_window_closed".into());
    }
    let h = window.hwnd as HWND;
    if window.native() {
        let full = display == "fullscreen";
        let (x, y, width, height) = if full {
            (monitor.x, monitor.y, monitor.width, monitor.height)
        } else {
            (
                monitor.work_x,
                monitor.work_y,
                (monitor.work_width * 4 / 5).max(320),
                (monitor.work_height * 4 / 5).max(240),
            )
        };
        // SAFETY: only the verified application's remote window is restyled and moved.
        unsafe {
            ShowWindow(h, SW_RESTORE);
            SetWindowLongPtrW(
                h,
                GWL_STYLE,
                if full {
                    WS_POPUP | WS_VISIBLE
                } else {
                    WS_OVERLAPPEDWINDOW | WS_VISIBLE
                } as isize,
            );
            if SetWindowPos(
                h,
                ptr::null_mut(),
                x,
                y,
                width,
                height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
            ) == 0
            {
                return Err("rdp_move_failed".into());
            }
            if display == "maximized" {
                ShowWindow(h, SW_MAXIMIZE);
            }
            if PostMessageW(h, crate::rdp_client::MODE_MESSAGE, usize::from(full), 0) == 0 {
                return Err("rdp_move_failed".into());
            }
        }
        if activate {
            activate_window(window)?;
        }
        return Ok(());
    }
    let mut rect = RECT::default();
    // SAFETY: validated window and valid output rectangle.
    unsafe {
        GetWindowRect(h, &mut rect);
    }
    // SAFETY: 窗口身份已验证；只读取样式判断 mstsc 自身维护的全屏状态。
    let was_full = unsafe { GetWindowLongPtrW(h, GWL_STYLE) as u32 & WS_CAPTION == 0 };
    if was_full
        && display == "fullscreen"
        && rect.left == monitor.x
        && rect.top == monitor.y
        && rect.right - rect.left == monitor.width
        && rect.bottom - rect.top == monitor.height
    {
        return if activate {
            activate_window(window)
        } else {
            Ok(())
        };
    }
    if was_full {
        toggle_fullscreen(window)?;
    }
    // SAFETY: 仅恢复身份已核对的远程窗口，跨屏前已退出 mstsc 全屏状态。
    unsafe {
        ShowWindow(h, SW_RESTORE);
    }
    let (x, y, w, height) = if display == "fullscreen" {
        (monitor.x, monitor.y, monitor.width, monitor.height)
    } else {
        (
            monitor.work_x,
            monitor.work_y,
            (monitor.work_width * 4 / 5).max(640),
            (monitor.work_height * 4 / 5).max(480),
        )
    };
    // SAFETY: handle is verified; no other application window is moved.
    if unsafe {
        SetWindowPos(
            h,
            ptr::null_mut(),
            x,
            y,
            w,
            height,
            SWP_NOZORDER | SWP_NOACTIVATE,
        )
    } == 0
    {
        return Err("rdp_move_failed".into());
    }
    if display == "maximized" {
        // SAFETY: verified window, documented show command.
        unsafe {
            ShowWindow(h, SW_MAXIMIZE);
        }
    }
    if display == "fullscreen" {
        toggle_fullscreen(window)?;
    }
    if activate {
        activate_window(window)?;
    }
    Ok(())
}
fn activate_window(window: &RemoteWindow) -> Result<(), String> {
    if !window.valid() {
        return Err("rdp_window_closed".into());
    }
    // SAFETY: 窗口身份已验证；激活后仍需核对前台身份才能发送组合键。
    unsafe {
        SetForegroundWindow(window.hwnd as HWND);
    }
    let deadline = Instant::now() + Duration::from_millis(500);
    while Instant::now() < deadline {
        if foreground() == window.hwnd {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(10));
    }
    Err("rdp_activate_failed".into())
}
fn toggle_fullscreen(window: &RemoteWindow) -> Result<(), String> {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
    let h = window.hwnd as HWND;
    activate_window(window)?;
    // SAFETY: activate_window 已验证目标窗口；此处只读取其样式。
    let previous_caption = unsafe { GetWindowLongPtrW(h, GWL_STYLE) as u32 & WS_CAPTION };
    for key in [VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN] {
        // SAFETY: querying a documented virtual key does not change input state.
        if unsafe { GetAsyncKeyState(key as i32) } < 0 {
            return Err("rdp_keys_busy".into());
        }
    }
    let keys = [
        (VK_CONTROL, 0),
        (VK_MENU, 0),
        (VK_CANCEL, KEYEVENTF_EXTENDEDKEY),
        (VK_CANCEL, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP),
        (VK_MENU, KEYEVENTF_KEYUP),
        (VK_CONTROL, KEYEVENTF_KEYUP),
    ];
    let inputs: Vec<INPUT> = keys
        .into_iter()
        .map(|(key, flags)| INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: key,
                    wScan: match key {
                        VK_CONTROL => 0x1d,
                        VK_MENU => 0x38,
                        VK_CANCEL => 0x46,
                        _ => 0,
                    },
                    dwFlags: flags,
                    ..Default::default()
                },
            },
        })
        .collect();
    // SAFETY: foreground identity was checked; array is correctly sized.
    if unsafe {
        SendInput(
            inputs.len() as u32,
            inputs.as_ptr(),
            std::mem::size_of::<INPUT>() as i32,
        )
    } != inputs.len() as u32
    {
        return Err("rdp_keys_failed".into());
    }
    let deadline = Instant::now() + Duration::from_secs(2);
    while Instant::now() < deadline {
        if !window.valid() {
            return Err("rdp_window_closed".into());
        }
        // SAFETY: 本次循环已验证窗口身份；只读取样式。
        if unsafe { GetWindowLongPtrW(h, GWL_STYLE) as u32 & WS_CAPTION } != previous_caption {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(20));
    }
    Err("rdp_keys_failed".into())
}
pub fn arrange(
    window: &RemoteWindow,
    dedicated: bool,
    saved_id: &mut Option<String>,
) -> Result<Option<String>, String> {
    if !window.valid() {
        return Err("rdp_window_closed".into());
    }
    let desktop = if dedicated {
        let existing = saved_id.as_deref().and_then(|id| {
            winvd::get_desktops()
                .ok()?
                .into_iter()
                .find(|d| d.get_id().ok().is_some_and(|g| format!("{g:?}") == id))
        });
        match existing {
            Some(d) => d,
            None => {
                let d = winvd::create_desktop().map_err(|_| "virtual_desktop_unavailable")?;
                *saved_id = Some(format!(
                    "{:?}",
                    d.get_id().map_err(|_| "virtual_desktop_unavailable")?
                ));
                // 名称设置失败不影响桌面的复用，后续失败仍保留已创建的 ID。
                let _ = d.set_name("NetDriveGuard");
                d
            }
        }
    } else {
        winvd::get_current_desktop().map_err(|_| "virtual_desktop_unavailable")?
    };
    winvd::move_window_to_desktop(
        desktop,
        &windows::Win32::Foundation::HWND(window.hwnd as *mut _),
    )
    .map_err(|_| "virtual_desktop_move_failed")?;
    if dedicated {
        winvd::switch_desktop(desktop).map_err(|_| "virtual_desktop_switch_failed")?;
        let expected = desktop
            .get_id()
            .map_err(|_| "virtual_desktop_switch_failed")?;
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            if winvd::get_current_desktop()
                .and_then(|d| d.get_id())
                .is_ok_and(|id| id == expected)
            {
                break;
            }
            if Instant::now() >= deadline {
                return Err("virtual_desktop_switch_failed".into());
            }
            thread::sleep(Duration::from_millis(20));
        }
    }
    Ok(if dedicated {
        Some(format!(
            "{:?}",
            desktop
                .get_id()
                .map_err(|_| "virtual_desktop_unavailable")?
        ))
    } else {
        None
    })
}

/// 验证非公开接口版本兼容性，只创建并移除本次空白测试桌面。
pub fn test_virtual_desktop() -> Result<(), String> {
    let original =
        winvd::get_current_desktop().map_err(|e| format!("virtual_desktop_unavailable: {e:?}"))?;
    let before = winvd::get_desktop_count().map_err(|e| format!("{e:?}"))?;
    let created = winvd::create_desktop().map_err(|e| format!("{e:?}"))?;
    let result = (|| {
        created
            .set_name("NetDriveGuard 验证")
            .map_err(|e| format!("{e:?}"))?;
        if winvd::get_desktop_count().map_err(|e| format!("{e:?}"))? != before + 1 {
            return Err("desktop_count_mismatch".into());
        }
        Ok(())
    })();
    winvd::remove_desktop(created, original).map_err(|e| format!("{e:?}"))?;
    result
}

pub fn hide_hint(handle: usize) {
    // SAFETY: handle 来自宿主自己的提示窗口；调用仅更新窗口可见性。
    unsafe {
        ShowWindow(handle as HWND, SW_HIDE);
    }
}

pub fn show_hint(handle: usize) {
    let foreground = foreground();
    if let Ok(desktop) =
        winvd::get_desktop_by_window(windows::Win32::Foundation::HWND(foreground as *mut _))
    {
        let _ = winvd::move_window_to_desktop(
            desktop,
            &windows::Win32::Foundation::HWND(handle as *mut _),
        );
    }
    // SAFETY: caller supplies its own live overlay; never activates this window.
    unsafe {
        SetWindowPos(
            handle as HWND,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
        );
        ShowWindow(handle as HWND, SW_SHOWNOACTIVATE);
    }
}

/// 测试清理仅接受精确桌面 ID，且拒绝移除当前桌面及包含可见应用窗口的桌面。
pub fn remove_empty_test_desktop(id: &str) -> Result<(), String> {
    let desktops = winvd::get_desktops().map_err(|e| format!("{e:?}"))?;
    let desktop = desktops
        .into_iter()
        .find(|d| d.get_id().is_ok_and(|g| format!("{g:?}") == id))
        .ok_or("desktop_not_found")?;
    let current = winvd::get_current_desktop().map_err(|e| format!("{e:?}"))?;
    let guid = desktop.get_id().map_err(|e| format!("{e:?}"))?;
    if current.get_id().map_err(|e| format!("{e:?}"))? == guid
        || !desktop
            .get_name()
            .map_err(|e| format!("{e:?}"))?
            .starts_with("NetDriveGuard")
    {
        return Err("desktop_cleanup_refused".into());
    }
    struct Check {
        id: String,
        occupied: bool,
    }
    unsafe extern "system" fn check(h: HWND, data: LPARAM) -> i32 {
        // SAFETY: EnumWindows 同步调用，指针指向本次栈上的检查状态。
        let state = unsafe { &mut *(data as *mut Check) };
        // SAFETY: h 由 EnumWindows 提供，只查询可见性。
        if unsafe { IsWindowVisible(h) } == 0
            || matches!(
                window_class(h).as_str(),
                "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd"
            )
        {
            return 1;
        }
        if let Ok(d) = winvd::get_desktop_by_window(windows::Win32::Foundation::HWND(h)) {
            if d.get_id().is_ok_and(|g| format!("{g:?}") == state.id) {
                state.occupied = true;
                return 0;
            }
        }
        1
    }
    let mut state = Check {
        id: id.into(),
        occupied: false,
    };
    // SAFETY: state 存活至同步枚举结束，回调只在此期间借用它。
    unsafe {
        EnumWindows(Some(check), (&mut state as *mut Check) as isize);
    }
    if state.occupied {
        return Err("desktop_not_empty".into());
    }
    winvd::remove_desktop(desktop, current).map_err(|e| format!("{e:?}"))
}
