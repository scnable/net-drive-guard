using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Diagnostics;

/// <summary>
/// 状态变化立即写心跳，稳定状态按最小间隔降频写入。
/// </summary>
public sealed class HeartbeatWritePolicy(TimeSpan minimumInterval)
{
    private readonly TimeSpan _minimumInterval = ValidateInterval(minimumInterval);
    private readonly Lock _sync = new();
    private DateTimeOffset? _lastWriteAt;
    private MonitorState? _lastState;

    public bool TryRegister(MonitorState state, DateTimeOffset completedAt)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "未知监控状态。");
        }

        lock (_sync)
        {
            var shouldWrite = _lastWriteAt is null
                || _lastState != state
                || completedAt < _lastWriteAt
                || completedAt - _lastWriteAt >= _minimumInterval;
            if (!shouldWrite)
            {
                return false;
            }

            _lastWriteAt = completedAt;
            _lastState = state;
            return true;
        }
    }

    private static TimeSpan ValidateInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(10) || interval > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "心跳写入间隔必须位于 10 秒到 10 分钟之间。");
        }

        return interval;
    }
}
