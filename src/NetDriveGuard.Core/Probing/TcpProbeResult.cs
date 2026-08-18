namespace NetDriveGuard.Core.Probing;

public sealed record TcpProbeResult(
    TcpProbeStatus Status,
    TimeSpan Duration,
    string Code)
{
    public bool IsReachable => Status == TcpProbeStatus.Reachable;
}
