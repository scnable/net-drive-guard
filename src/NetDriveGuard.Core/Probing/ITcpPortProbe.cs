namespace NetDriveGuard.Core.Probing;

public interface ITcpPortProbe
{
    Task<TcpProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}
