use crate::{
    settings::{self, Settings},
    Event,
};
use std::{
    collections::VecDeque,
    fs::{self, OpenOptions},
    io::{BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
};
const FILES: [&str; 10] = [
    "settings.json",
    "host.json",
    "preferences.json",
    "host-events.json",
    "remote-window.json",
    "desktop.rdp",
    "events.jsonl",
    "events.1.jsonl",
    "events.2.jsonl",
    "events.3.jsonl",
];
static EVENT_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
pub const LOG_LIMIT: u64 = 5 * 1024 * 1024;
pub struct Storage {
    pub directory: PathBuf,
    pub root: PathBuf,
    pub log_limit: u64,
}
pub fn atomic(path: &Path, value: &impl serde::Serialize) -> Result<(), String> {
    let parent = path.parent().ok_or("invalid_config_directory")?;
    fs::create_dir_all(parent).map_err(|_| "config_write_failed")?;
    let mut file = tempfile::NamedTempFile::new_in(parent).map_err(|_| "config_write_failed")?;
    serde_json::to_writer_pretty(&mut file, value).map_err(|_| "config_write_failed")?;
    file.as_file()
        .sync_all()
        .map_err(|_| "config_write_failed")?;
    file.persist(path).map_err(|_| "config_write_failed")?;
    Ok(())
}
impl Storage {
    pub fn open(root: PathBuf) -> (Self, Option<String>) {
        let pointer = root.join("storage-path.json");
        let mut directory = root.clone();
        let mut error = None;
        if pointer.exists() {
            match fs::read(&pointer)
                .ok()
                .and_then(|b| serde_json::from_slice::<String>(&b).ok())
                .map(PathBuf::from)
            {
                Some(path) if validate_directory(&path, None).is_ok() => directory = path,
                _ => error = Some("storage_pointer_invalid".into()),
            }
        }
        (
            Self {
                directory,
                root,
                log_limit: LOG_LIMIT,
            },
            error,
        )
    }
    pub fn load(&self) -> Result<Option<Settings>, String> {
        let path = self.directory.join("settings.json");
        let path = if !path.exists() && self.directory.join("config.json").exists() {
            self.directory.join("config.json")
        } else {
            path
        };
        let file = match fs::File::open(path) {
            Ok(f) => f,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(_) => return Err("config_read_failed".into()),
        };
        let mut bytes = Vec::new();
        file.take(65537)
            .read_to_end(&mut bytes)
            .map_err(|_| "config_read_failed")?;
        if bytes.len() > 65536 {
            return Err("config_invalid".into());
        }
        settings::decode(&bytes).map(Some)
    }
    pub fn save(&mut self, settings: &Settings, destination: Option<&Path>) -> Result<(), String> {
        let destination = destination.unwrap_or(&self.directory).to_path_buf();
        let destination = destination.as_path();
        validate_directory(destination, Some(&settings.drive))?;
        let document = serde_json::json!({"version":1,"settings":settings});
        if same_directory(destination, &self.directory) {
            atomic(&self.directory.join("settings.json"), &document)?;
            let pointer = self.root.join("storage-path.json");
            if same_directory(destination, &self.root) && pointer.exists() {
                atomic(&pointer, &destination.to_string_lossy())?;
            }
            return Ok(());
        }
        self.move_to(destination, Some(&document))
    }
    pub fn relocate(&mut self, destination: &Path, drive: Option<&str>) -> Result<(), String> {
        validate_directory(destination, drive)?;
        if same_directory(destination, &self.directory) {
            atomic(
                &self.root.join("storage-path.json"),
                &self.directory.to_string_lossy(),
            )?;
            return Ok(());
        }
        self.move_to(destination, None)
    }
    fn move_to(
        &mut self,
        destination: &Path,
        settings: Option<&serde_json::Value>,
    ) -> Result<(), String> {
        let names: Vec<_> = FILES.iter().copied().chain(["config.json"]).collect();
        if names.iter().any(|f| destination.join(f).exists()) {
            return Err("storage_destination_conflict".into());
        }
        let existed = destination.exists();
        fs::create_dir_all(destination).map_err(|_| "config_write_failed")?;
        let mut created = Vec::new();
        let result = (|| {
            for name in names {
                let source = self.directory.join(name);
                if !(source.exists() || name == "settings.json" && settings.is_some()) {
                    continue;
                }
                let path = destination.join(name);
                let mut output = OpenOptions::new()
                    .create_new(true)
                    .write(true)
                    .open(&path)
                    .map_err(|_| "storage_destination_conflict")?;
                created.push(path);
                if let Some(document) = settings.filter(|_| name == "settings.json") {
                    serde_json::to_writer_pretty(&mut output, document)
                        .map_err(|_| "config_write_failed")?;
                } else {
                    let mut input = fs::File::open(source).map_err(|_| "config_read_failed")?;
                    std::io::copy(&mut input, &mut output).map_err(|_| "config_write_failed")?;
                }
                output.sync_all().map_err(|_| "config_write_failed")?;
            }
            atomic(
                &self.root.join("storage-path.json"),
                &destination.to_string_lossy(),
            )?;
            Ok(())
        })();
        if result.is_err() {
            for path in created {
                let _ = fs::remove_file(path);
            }
            if !existed {
                let _ = fs::remove_dir(destination);
            }
        } else {
            self.directory = destination.to_path_buf();
        }
        result
    }
    pub fn append(&self, event: &Event) -> Result<(), String> {
        let _guard = EVENT_LOCK.lock().map_err(|_| "events_write_failed")?;
        fs::create_dir_all(&self.directory).map_err(|_| "events_write_failed")?;
        let path = self.directory.join("events.jsonl");
        let bytes = serde_json::to_vec(event).map_err(|_| "events_write_failed")?;
        if fs::metadata(&path)
            .map(|m| m.len() + bytes.len() as u64 + 1 > self.log_limit)
            .unwrap_or(false)
        {
            let oldest = self.directory.join("events.3.jsonl");
            if oldest.exists() {
                fs::remove_file(oldest).map_err(|_| "events_write_failed")?;
            }
            for n in (1..3).rev() {
                let source = self.directory.join(format!("events.{n}.jsonl"));
                if source.exists() {
                    fs::rename(
                        source,
                        self.directory.join(format!("events.{}.jsonl", n + 1)),
                    )
                    .map_err(|_| "events_write_failed")?;
                }
            }
            if path.exists() {
                fs::rename(&path, self.directory.join("events.1.jsonl"))
                    .map_err(|_| "events_write_failed")?;
            }
        }
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .map_err(|_| "events_write_failed")?;
        file.write_all(&bytes)
            .and_then(|_| file.write_all(b"\n"))
            .map_err(|_| "events_write_failed".into())
    }
    pub fn recent(&self) -> VecDeque<Event> {
        let _guard = EVENT_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let mut events = VecDeque::new();
        for name in [
            "events.3.jsonl",
            "events.2.jsonl",
            "events.1.jsonl",
            "events.jsonl",
        ] {
            if let Ok(file) = fs::File::open(self.directory.join(name)) {
                for line in BufReader::new(file.take(LOG_LIMIT + 65536))
                    .lines()
                    .map_while(Result::ok)
                {
                    if let Ok(event) = serde_json::from_str::<Event>(&line) {
                        events.push_front(event);
                        events.truncate(100);
                    }
                }
            }
        }
        // 兼容旧版单独保存的主机记录；已进入统一日志的记录按时间和代码去重。
        let legacy = self.directory.join("host-events.json");
        if fs::metadata(&legacy).is_ok_and(|m| m.len() < 256 * 1024) {
            if let Ok(bytes) = fs::read(legacy) {
                if let Ok(old) = serde_json::from_slice::<Vec<crate::host::HostEvent>>(&bytes) {
                    for item in old {
                        let event = item.as_event();
                        if !events.iter().any(|e| {
                            e.at_ms == event.at_ms
                                && e.code == event.code
                                && e.source == event.source
                        }) {
                            events.push_back(event);
                        }
                    }
                }
            }
        }
        for event in &mut events {
            if event.source.is_empty() {
                event.source = "mapping".into();
            }
        }
        events
            .make_contiguous()
            .sort_by_key(|a| std::cmp::Reverse(a.at_ms));
        events.truncate(100);
        events
    }
}
fn same_directory(left: &Path, right: &Path) -> bool {
    if let (Ok(left), Ok(right)) = (left.canonicalize(), right.canonicalize()) {
        if left == right {
            return true;
        }
    }
    if cfg!(windows) {
        left.to_string_lossy()
            .eq_ignore_ascii_case(&right.to_string_lossy())
    } else {
        left == right
    }
}
pub fn validate_directory(path: &Path, drive: Option<&str>) -> Result<(), String> {
    if !path.is_absolute() || path.to_string_lossy().starts_with("\\\\") {
        return Err("storage_local_only".into());
    }
    #[cfg(windows)]
    {
        let text = path.to_string_lossy();
        let prefix = text.get(..2).ok_or("storage_local_only")?;
        if drive.is_some_and(|d| d.eq_ignore_ascii_case(prefix)) {
            return Err("storage_monitored_drive".into());
        }
        if !net_drive_guard_windows::is_local_directory(path) {
            return Err("storage_local_only".into());
        }
    }
    #[cfg(not(windows))]
    let _ = drive;
    Ok(())
}
