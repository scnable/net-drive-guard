namespace NetDriveGuard.Core.Scheduling;

public sealed record ScheduleSnapshot(
    DateTimeOffset? LastLocalMappingQueryAt = null,
    DateTimeOffset? LastTcpProbeAt = null,
    DateTimeOffset? LastShareProbeAt = null);
