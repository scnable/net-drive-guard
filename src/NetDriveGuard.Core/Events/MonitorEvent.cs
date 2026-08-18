using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Events;

public sealed record MonitorEvent
{
    public MonitorEvent(
        DateTimeOffset occurredAt,
        MonitorState state,
        MonitorEventSeverity severity,
        string code,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "未知监控状态。");
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "未知事件级别。");
        }

        if (code.Length > 64
            || code.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ArgumentException("事件代码格式无效。", nameof(code));
        }

        if (description.Length > 200
            || description.Contains('\r', StringComparison.Ordinal)
            || description.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("事件描述格式无效。", nameof(description));
        }

        OccurredAt = occurredAt;
        State = state;
        Severity = severity;
        Code = code;
        Description = description;
    }

    public DateTimeOffset OccurredAt { get; }

    public MonitorState State { get; }

    public MonitorEventSeverity Severity { get; }

    public string Code { get; }

    public string Description { get; }
}
