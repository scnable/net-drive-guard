namespace NetDriveGuard.Core.Notifications;

/// <summary>
/// 在状态机变化通知之外提供第二层时间冷却，防止异常抖动刷屏。
/// </summary>
public sealed class NotificationGate(TimeSpan cooldown)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPublishedAt = new(StringComparer.Ordinal);
    private readonly TimeSpan _cooldown = ValidateCooldown(cooldown);

    public bool TryRegister(string key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            if (_lastPublishedAt.TryGetValue(key, out var lastPublished)
                && now >= lastPublished
                && now - lastPublished < _cooldown)
            {
                return false;
            }

            _lastPublishedAt[key] = now;
            return true;
        }
    }

    public void Reset(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            _lastPublishedAt.Remove(key);
        }
    }

    private static TimeSpan ValidateCooldown(TimeSpan cooldown)
    {
        if (cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), "通知冷却时间不能为负数。");
        }

        return cooldown;
    }
}
