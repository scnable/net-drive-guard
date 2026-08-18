namespace NetDriveGuard.Windows;

using NetDriveGuard.Core.Abstractions;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SystemDelayProvider : IDelayProvider
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}

/// <summary>
/// 通过当前用户会话内的命名互斥量防止重复实例。
/// </summary>
public sealed class NamedMutexSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public NamedMutexSingleInstanceGuard(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var safeName = string.Concat(applicationId.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_'));

        _mutex = new Mutex(true, $@"Local\{safeName}", out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (IsPrimaryInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _disposed = true;
    }
}
