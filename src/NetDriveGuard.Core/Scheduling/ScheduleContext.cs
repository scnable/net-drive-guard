using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Scheduling;

public sealed record ScheduleContext(
    MonitorState State,
    DateTimeOffset Now,
    ScheduleTrigger Trigger,
    ScheduleSnapshot Snapshot,
    bool HasConfiguration = false);
