namespace NetDriveGuard.Core.Monitoring;

public sealed record MonitorPolicy(
    int OfflineFailureThreshold = 3,
    bool RemoveMappingWhenOffline = true,
    bool RepairMissingMapping = true)
{
    public void EnsureValid()
    {
        if (OfflineFailureThreshold is < 2 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OfflineFailureThreshold),
                "离线确认次数必须位于 2 到 10 之间。");
        }
    }
}
