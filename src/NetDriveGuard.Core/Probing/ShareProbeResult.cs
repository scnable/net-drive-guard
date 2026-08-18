namespace NetDriveGuard.Core.Probing;

public sealed record ShareProbeResult(
    ShareProbeStatus Status,
    TimeSpan Duration,
    string Code);
