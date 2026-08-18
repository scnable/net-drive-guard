namespace NetDriveGuard.Core.Monitoring;

/// <summary>
/// 经过外部适配层归一化的观察结果，不携带密码和完整错误正文。
/// </summary>
public sealed record MonitorObservation(
    ObservationStatus Status,
    DateTimeOffset ObservedAt,
    string Code = "observation.completed");
