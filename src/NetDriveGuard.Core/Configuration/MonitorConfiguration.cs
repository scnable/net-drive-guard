using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Configuration;

/// <summary>
/// 仅包含非敏感配置。密码不得进入此模型。
/// </summary>
public sealed record MonitorConfiguration(
    string DriveLetter,
    string SharePath,
    MonitoringProfile Profile = MonitoringProfile.Default,
    TimeSpan? CustomTcpInterval = null,
    int OfflineFailureThreshold = 3,
    bool RemoveMappingWhenOffline = false,
    TimeSpan? TcpTimeout = null,
    TimeSpan? OverallCheckTimeout = null)
{
    public TimeSpan EffectiveTcpTimeout => TcpTimeout ?? TimeSpan.FromMilliseconds(800);

    public TimeSpan EffectiveOverallCheckTimeout => OverallCheckTimeout ?? TimeSpan.FromSeconds(10);
}
