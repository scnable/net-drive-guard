namespace NetDriveGuard.Core.Scheduling;

public enum SystemChangeSignal
{
    None = 0,
    NetworkChanged = 1,
    PowerResumed = 2,
}

/// <summary>
/// 合并来自系统线程的网络和唤醒信号，由 UI 调度线程安全取出。
/// </summary>
public sealed class SystemChangeSignalQueue
{
    private int _pendingSignal;

    public void Publish(SystemChangeSignal signal)
    {
        var priority = signal switch
        {
            SystemChangeSignal.NetworkChanged => 1,
            SystemChangeSignal.PowerResumed => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(signal), "只能发布网络变化或系统唤醒信号。"),
        };

        while (true)
        {
            var current = Volatile.Read(ref _pendingSignal);
            if (current >= priority)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _pendingSignal, priority, current) == current)
            {
                return;
            }
        }
    }

    public bool TryTake(out SystemChangeSignal signal)
    {
        signal = Interlocked.Exchange(ref _pendingSignal, 0) switch
        {
            1 => SystemChangeSignal.NetworkChanged,
            2 => SystemChangeSignal.PowerResumed,
            _ => SystemChangeSignal.None,
        };

        return signal != SystemChangeSignal.None;
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _pendingSignal, 0);
    }
}
