namespace NetDriveGuard.Core.Abstractions;

public interface ISingleInstanceGuard : IDisposable
{
    bool IsPrimaryInstance { get; }
}
