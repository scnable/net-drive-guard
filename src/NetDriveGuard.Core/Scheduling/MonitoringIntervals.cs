namespace NetDriveGuard.Core.Scheduling;

public sealed record MonitoringIntervals(
    TimeSpan LocalMappingQuery,
    TimeSpan TcpProbe,
    TimeSpan FailureRetry,
    TimeSpan OfflineRecovery,
    TimeSpan ShareProbe)
{
    public static readonly TimeSpan MinimumCustomInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumCustomInterval = TimeSpan.FromMinutes(30);

    public static MonitoringIntervals ForProfile(
        MonitoringProfile profile,
        TimeSpan? customTcpInterval = null)
    {
        return profile switch
        {
            MonitoringProfile.Default => new(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(45)),

            MonitoringProfile.Fast => new(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30)),

            MonitoringProfile.EnergySaving => new(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(60)),

            MonitoringProfile.Custom => CreateCustom(customTcpInterval),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "未知检测模式。"),
        };
    }

    public static bool IsCustomIntervalValid(TimeSpan interval)
    {
        return interval >= MinimumCustomInterval && interval <= MaximumCustomInterval;
    }

    private static MonitoringIntervals CreateCustom(TimeSpan? customTcpInterval)
    {
        if (customTcpInterval is null || !IsCustomIntervalValid(customTcpInterval.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(customTcpInterval),
                "自定义检测周期必须位于 1 秒到 30 分钟之间。");
        }

        var tcp = customTcpInterval.Value;
        var local = tcp <= TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(2);
        var offlineSeconds = Math.Clamp(tcp.TotalSeconds, 5, 10);
        var shareSeconds = Math.Clamp(tcp.TotalSeconds * 6, 30, 60);

        return new MonitoringIntervals(
            local,
            tcp,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(offlineSeconds),
            TimeSpan.FromSeconds(shareSeconds));
    }
}
