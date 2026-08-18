using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.Core.Runtime;

public sealed record ScheduledMonitorRunResult(
    ScheduleTrigger Trigger,
    CheckLayer RequestedChecks,
    MonitorRunResult Run);
