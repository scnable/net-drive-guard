namespace NetDriveGuard.Core.Events;

/// <summary>
/// 进程内有界事件缓冲；最新事件在前，连续相同事件只保留一条。
/// </summary>
public sealed class RecentEventBuffer
{
    private readonly object _syncRoot = new();
    private readonly List<MonitorEvent> _events;
    private readonly int _capacity;

    public RecentEventBuffer(int capacity = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _events = new List<MonitorEvent>(capacity);
    }

    public bool TryAdd(MonitorEvent monitorEvent)
    {
        ArgumentNullException.ThrowIfNull(monitorEvent);

        lock (_syncRoot)
        {
            if (_events.Count > 0
                && _events[0].State == monitorEvent.State
                && string.Equals(_events[0].Code, monitorEvent.Code, StringComparison.Ordinal))
            {
                return false;
            }

            _events.Insert(0, monitorEvent);
            if (_events.Count > _capacity)
            {
                _events.RemoveAt(_events.Count - 1);
            }

            return true;
        }
    }

    public IReadOnlyList<MonitorEvent> Snapshot()
    {
        lock (_syncRoot)
        {
            return _events.ToArray();
        }
    }
}
