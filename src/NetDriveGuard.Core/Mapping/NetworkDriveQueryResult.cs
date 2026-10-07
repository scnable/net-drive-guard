namespace NetDriveGuard.Core.Mapping;

public sealed record NetworkDriveQueryResult(
    NetworkDriveQueryStatus Status,
    string? RemotePath = null,
    int NativeErrorCode = 0)
{
    public bool IsMapped => Status is NetworkDriveQueryStatus.Mapped or NetworkDriveQueryStatus.Disconnected;
}
