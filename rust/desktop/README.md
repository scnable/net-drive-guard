# NetDriveGuard 桌面构建

Tauri 2 Windows 宿主，Vue 3 + TypeScript 界面。配置、状态与操作由 Rust 管理，刷新界面不会重启监控。

## 环境

构建 Windows x64 EXE 需要 Rust MSVC 工具链、Node.js、MSVC Build Tools、Windows SDK 和 Microsoft Edge WebView2 Runtime。当前版本使用 Rust 1.99.0、Node.js 24 构建。

## 前端

在 `rust/desktop` 目录执行：

```bash
npm ci
npm run build
```

`npm run dev` 提供浏览器布局预览。预览不能读取或修改 Windows 状态。

## Windows EXE

在 Windows 的 `rust` 目录执行：

```powershell
$work = Join-Path $env:TEMP ('netDriveGuard-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
cargo fetch --locked --manifest-path desktop/src-tauri/Cargo.toml
.\desktop\tools\build-windows.ps1 -WorkDirectory $work -Release
.\desktop\tools\package-windows.ps1 -TargetDir (Join-Path $work 'target') -OutputDirectory (Join-Path $PWD 'artifacts/netDriveGuard')
```

构建前须完成前端构建。Windows 脚本使用锁定依赖离线编译，并执行 Clippy；省略 `-Release` 生成 Debug。前端资源通过逐文件 SHA-256 清单复制与校验，编译输出和日志位于本次临时目录。

输出目录包含单文件 `NetDriveGuard.exe` 和 `NetDriveGuard.sha256.json`，不生成压缩包。更新输出目录中的已有 EXE 时增加 `-Force`。文件被占用时使用另一个输出目录，保留运行中的程序和用户数据。

确认输出文件和日志已保存后，可删除本次 `$work` 临时目录。同一目录中的构建和打包应顺序执行。

## 使用

默认窗口为 760×620，支持缩放和小窗口布局。网络盘维护、开发主机连接及数据目录设置见[使用说明](USAGE.md)。
