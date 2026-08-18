namespace NetDriveGuard.Core.Mapping;

public interface INetworkDriveQuery
{
    NetworkDriveQueryResult Query(string driveLetter);
}
