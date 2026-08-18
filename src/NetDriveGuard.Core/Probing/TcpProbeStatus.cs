namespace NetDriveGuard.Core.Probing;

public enum TcpProbeStatus
{
    Reachable,
    Refused,
    TimedOut,
    NameResolutionFailed,
    Unreachable,
    Failed,
}
