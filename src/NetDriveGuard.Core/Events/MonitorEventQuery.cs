using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Events;

public static class MonitorEventQuery
{
    public static IReadOnlyList<MonitorEvent> Apply(
        IEnumerable<MonitorEvent> events,
        MonitorEventFilter filter)
    {
        return Apply(events, filter, state: null);
    }

    public static IReadOnlyList<MonitorEvent> Apply(
        IEnumerable<MonitorEvent> events,
        MonitorEventFilter filter,
        MonitorState? state)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (state.HasValue && !Enum.IsDefined(state.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "未知监控状态。");
        }

        var query = filter switch
        {
            MonitorEventFilter.All => events,
            MonitorEventFilter.WarningAndError =>
                events.Where(monitorEvent =>
                    monitorEvent.Severity is
                        MonitorEventSeverity.Warning or MonitorEventSeverity.Error),
            MonitorEventFilter.ErrorOnly =>
                events.Where(monitorEvent =>
                    monitorEvent.Severity == MonitorEventSeverity.Error),
            _ => throw new ArgumentOutOfRangeException(
                nameof(filter),
                filter,
                "未知事件筛选条件。"),
        };

        if (state.HasValue)
        {
            query = query.Where(monitorEvent => monitorEvent.State == state.Value);
        }

        return query.ToArray();
    }
}
