using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Events;

/// <summary>
/// 把状态机决策转换为脱敏事件。描述只来自固定映射，不包含探测目标或异常原文。
/// </summary>
public static class MonitorEventFactory
{
    public static MonitorEvent? Create(MonitorDecision decision, MonitorState previousState)
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
        return new MonitorEvent(
            decision.Snapshot.LastObservationAt,
            state,
            severity,
            code,
            description);
    }

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
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知监控状态。"),
        };
    }
}
