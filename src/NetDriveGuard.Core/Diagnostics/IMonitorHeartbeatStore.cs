namespace NetDriveGuard.Core.Diagnostics;

public interface IMonitorHeartbeatStore
{
    Task WriteAsync(
        MonitorHeartbeat heartbeat,
        CancellationToken cancellationToken = default);
}
