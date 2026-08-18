using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.App;

/// <summary>
/// 设置功能完成前使用的安全探测器，不访问网络或 Windows 映射。
/// </summary>
internal sealed class UnconfiguredMonitorProbe(IClock clock) : ILayeredProbe<MonitorObservation>
{
    public Task<MonitorObservation> ObserveAsync(
        CheckLayer checkLayers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new MonitorObservation(
                ObservationStatus.NotConfigured,
                clock.UtcNow,
                "probe.not-configured"));
    }
}

/// <summary>
/// M2 只读阶段的安全边界：即使收到意外动作也拒绝修改系统状态。
/// </summary>
internal sealed class ReadOnlyRemediation : IRemediation<MonitorAction>
{
    public Task<RemediationResult> ExecuteAsync(
        MonitorAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RemediationResult.Failed("remediation.disabled-read-only"));
    }
}
