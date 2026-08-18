namespace NetDriveGuard.Core.Abstractions;

public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
