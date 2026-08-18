namespace NetDriveGuard.Core.Events;

public interface IMonitorEventStore
{
    Task AppendAsync(
        MonitorEvent monitorEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitorEvent>> LoadRecentAsync(
        int maxCount,
        CancellationToken cancellationToken = default);
}
