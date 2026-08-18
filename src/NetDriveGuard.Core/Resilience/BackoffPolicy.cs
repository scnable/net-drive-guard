namespace NetDriveGuard.Core.Resilience;

public static class BackoffPolicy
{
    public static TimeSpan GetDelay(FailureCategory category, int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailures),
                "连续失败次数必须大于零。");
        }

        var (initial, maximum) = category switch
        {
            FailureCategory.Network => (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)),
            FailureCategory.Authentication => (TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)),
            FailureCategory.Internal => (TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知失败分类。"),
        };

        var delayTicks = initial.Ticks;
        var maximumTicks = maximum.Ticks;
        var doublings = Math.Min(consecutiveFailures - 1, 30);

        for (var index = 0; index < doublings && delayTicks < maximumTicks; index++)
        {
            delayTicks = Math.Min(delayTicks * 2, maximumTicks);
        }

        return TimeSpan.FromTicks(delayTicks);
    }
}
