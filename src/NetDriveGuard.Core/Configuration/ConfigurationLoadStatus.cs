namespace NetDriveGuard.Core.Configuration;

public enum ConfigurationLoadStatus
{
    Loaded,
    Missing,
    Invalid,
    UnsupportedVersion,
    StorageError,
}
