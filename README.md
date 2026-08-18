# NetDriveGuard

NetDriveGuard 是一个面向 Windows 当前用户会话的网络驱动器监控工具。它通过托盘图标持续展示执行状态，支持手动检查、暂停、恢复、状态变化提醒以及最近事件查询。

当前版本处于开发预览阶段，默认采用只读观察策略，不会自动建立、修复或删除网络驱动器映射。

## 当前能力

- 单实例托盘运行。
- WNet 网络驱动器映射只读查询。
- TCP 端口短连接探测。
- 共享目录可访问性只读探测与超时隔离。
- 动态分层检测、离线确认和恢复判定。
- 状态变化通知及通知冷却。
- 非敏感配置保存与运行时热应用。
- 脱敏事件持久化和最近 100 条事件恢复。
- 按严重级别和监控状态组合筛选事件。
- `--demo` 安全演示模式和 `--smoke-test` 无窗口启动检查。

## 安全边界

- 配置界面不提供用户名或密码字段。
- 配置、通知和事件不记录密码或异常原文。
- 普通模式当前只观察，不修改网络驱动器映射。
- 演示模式不访问网络，也不写入事件文件。
- 事件文件当前只追加，不自动删除或覆盖历史内容。

## 构建要求

- Windows 10 或更高版本。
- .NET SDK 10.0.400。

```powershell
dotnet build NetDriveGuard.sln -c Release
dotnet test NetDriveGuard.sln -c Release
```

生成 Windows x64 自包含单文件预览：

```powershell
.\scripts\publish-preview.ps1 -OutputDirectory artifacts\NetDriveGuard-preview
```

发布脚本拒绝覆盖已经存在的输出目录。

## 运行方式

普通启动会进入系统托盘：

```powershell
.\NetDriveGuard.App.exe
```

安全演示：

```powershell
.\NetDriveGuard.App.exe --demo
```

烟雾测试：

```powershell
.\NetDriveGuard.App.exe --smoke-test
```

关闭状态面板只会隐藏窗口。需要完全退出时，请右键托盘图标并选择“退出”。

## 当前限制

- 尚未实现 Windows 凭据管理器集成。
- 尚未实现映射建立、自动修复和取消。
- 尚未完成真实 SMB、登录启动、长时间运行和干净目标机验证。
- 事件文件尚未实现自动轮转和诊断导出。
- 开发预览尚未进行数字签名。

因此，当前版本适合只读监控和功能体验，尚不能替代已有的自动映射任务。
