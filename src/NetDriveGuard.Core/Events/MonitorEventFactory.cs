using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Abstractions;

namespace NetDriveGuard.Core.Events;

/// <summary>
/// 把状态机决策转换为脱敏事件。描述只来自固定映射，不包含探测目标或异常原文。
/// </summary>
public static class MonitorEventFactory
{
    public static MonitorEvent? Create(MonitorDecision decision, MonitorState previousState,
        RemediationResult? remediation = null)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var state = decision.Snapshot.CurrentState;
        if (!decision.ShouldNotify && state == previousState)
        {
            return null;
        }

        var recovered = string.Equals(
            decision.EventCode,
            "state.recovered",
            StringComparison.Ordinal);
        var (severity, code, description) = Describe(state, recovered);
        if (state == MonitorState.MappingFailed)
        {
            code = decision.EventCode;
            description = DescribeMappingFailure(code);
            if (remediation?.NativeErrorCode is int nativeError)
            {
                // 保留数字错误码，不记录共享地址、账号或密码。
                code += $".win32-{nativeError}";
            }
        }
        return new MonitorEvent(
            decision.Snapshot.LastObservationAt,
            state,
            severity,
            code,
            description);
    }

    public static string DescribeMappingFailure(string code) => code switch
    {
        "mapping.in-use" => "目标切换或断开失败：旧网络盘正在使用，请关闭相关文件后等待自动重试。",
        "mapping.authentication-failed" => "连接失败：当前目标的凭据无效或权限不足，请在设置中更新账号和密码。",
        "mapping.credential-conflict" => "连接失败：Windows 已使用其他账号连接该服务器。",
        "mapping.path-unavailable" => "连接失败：当前服务器或共享名称不可用，请检查共享地址。",
        "mapping.disconnect-failed" => "旧映射未能正常断开，未强制关闭文件，将自动重试。",
        "credential.read-failed" => "当前目标的已保存凭据读取失败，请在设置中重新保存凭据。",
        _ => "网络盘操作失败，将按当前配置自动重试；请查看事件代码。",
    };

    private static (MonitorEventSeverity Severity, string Code, string Description) Describe(
        MonitorState state,
        bool recovered)
    {
        if (recovered && state == MonitorState.Healthy)
        {
            return (
                MonitorEventSeverity.Information,
                "state.recovered",
                "监控目标已恢复正常。");
        }

        return state switch
        {
            MonitorState.NotConfigured =>
                (MonitorEventSeverity.Information, "state.not-configured", "尚未配置监控目标。"),
            MonitorState.Checking =>
                (MonitorEventSeverity.Warning, "state.checking", "检测到异常，正在继续确认。"),
            MonitorState.Healthy =>
                (MonitorEventSeverity.Information, "state.healthy", "监控目标状态正常。"),
            MonitorState.ServerOffline =>
                (MonitorEventSeverity.Error, "state.server-offline", "服务器当前不可达。"),
            MonitorState.MappingMissing =>
                (MonitorEventSeverity.Warning, "state.mapping-missing", "网络驱动器映射缺失。"),
            MonitorState.Repairing =>
                (MonitorEventSeverity.Information, "state.repairing", "正在处理网络驱动器映射。"),
            MonitorState.AuthenticationFailed =>
                (MonitorEventSeverity.Error, "state.authentication-failed", "共享访问认证失败。"),
            MonitorState.ShareUnavailable =>
                (MonitorEventSeverity.Warning, "state.share-unavailable", "共享目录当前不可用。"),
            MonitorState.DriveConflict =>
                (MonitorEventSeverity.Error, "state.drive-conflict", "目标盘符存在映射冲突。"),
            MonitorState.Paused =>
                (MonitorEventSeverity.Information, "state.paused", "监控已暂停。"),
            MonitorState.InternalError =>
                (MonitorEventSeverity.Error, "state.internal-error", "监控过程中发生内部错误。"),
            MonitorState.MappingFailed =>
                (MonitorEventSeverity.Error, "state.mapping-failed", "网络盘操作失败，将按当前配置自动重试；请查看事件代码。"),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知监控状态。"),
        };
    }
}
