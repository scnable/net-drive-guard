namespace NetDriveGuard.Core.Monitoring;

public sealed record MonitorDecision(
    MonitorSnapshot Snapshot,
    MonitorAction Action,
    bool ShouldNotify,
    string EventCode);
