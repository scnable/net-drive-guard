using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Diagnostics;

public sealed record MonitorHeartbeat(
    string ProductVersion,
    DateTimeOffset LastCompletedAt,
    MonitorState CurrentState,
    MonitorState LastStableState,
    int ConsecutiveServerFailures,
    DateTimeOffset LastTransitionAt,
    DateTimeOffset LastObservationAt,
    string ResultCode)
{
    public static MonitorHeartbeat FromSnapshot(
        string productVersion,
        MonitorSnapshot snapshot,
        DateTimeOffset completedAt,
        string resultCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultCode);

        return new MonitorHeartbeat(
            productVersion,
            completedAt,
            snapshot.CurrentState,
            snapshot.LastStableState,
            snapshot.ConsecutiveServerFailures,
            snapshot.LastTransitionAt,
            snapshot.LastObservationAt,
            resultCode);
    }
}
