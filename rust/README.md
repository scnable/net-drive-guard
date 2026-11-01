# NetDriveGuard Rust

Rust 核心与 Tauri 2 Windows 桌面宿主，Vue 3 + TypeScript 界面。

## 结构

| 目录 | 职责 |
| --- | --- |
| `src` | 配置、状态判断、分层检测调度和映射操作规划 |
| `windows` | Windows 映射、凭据、系统事件、登录任务、远程桌面及工作进程 |
| `app` | 配置存储、事件日志、监控协调和脱敏诊断 |
| `desktop` | Tauri 宿主、Vue 界面、托盘和系统提醒 |
| `tools` | Windows 本地构建目录与 Cargo 命令封装 |

网络盘维护与开发主机监测分别配置、检测和暂停。远程连接使用独立进程托管 Windows 远程桌面控件；主程序退出后保留连接，再次启动可接管原窗口。

## 核心检查

在 `rust` 目录执行：

```bash
cargo fetch --locked
cargo check --locked --offline --workspace --all-targets
cargo fmt --all --check
cargo clippy --locked --offline --workspace --all-targets -- -D warnings
```

核心和应用逻辑可在 Linux 检查；Windows 映射、凭据和桌面操作需要 Windows。桌面 crate 使用独立 workspace，构建方法见[桌面构建说明](desktop/README.md)。测试源码保留在私有仓库。

## 运行规则

映射、TCP 和共享分别按周期检查；确认恢复需要正确的映射与共享访问结果。网络变化、恢复唤醒、手动检查及设置保存触发完整核对。暂停后的过期结果不会启动映射操作。

映射操作使用当前配置目标和对应凭据，写入前再次核对盘符。本地磁盘、查询失败及外部修改会阻止操作。取消映射采用非强制模式，不关闭占用文件；连接 API 返回成功后仍会核对实际映射与共享访问。

阻塞 Windows 操作在受超时约束的工作进程内执行，超时后回收进程树。超时操作的结果记为未确认，后续检查再次核对实际状态。

默认日志单份最多 5 MiB，保留 3 个归档；事件页面显示最近 100 条记录。配置与日志目录可以独立迁移，不要求配置网络盘。操作方法见[使用说明](desktop/USAGE.md)。
