namespace NetDriveGuard.Core.Monitoring;

public sealed record MonitorSnapshot(
    MonitorState CurrentState,
    MonitorState LastStableState,
    int ConsecutiveServerFailures,
    DateTimeOffset LastTransitionAt,
    DateTimeOffset LastObservationAt)
{
    public static MonitorSnapshot Initial(DateTimeOffset now) => new(
        MonitorState.NotConfigured,
        MonitorState.NotConfigured,
        0,
        now,
        now);
}
