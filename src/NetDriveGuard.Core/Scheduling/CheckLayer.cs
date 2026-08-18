namespace NetDriveGuard.Core.Scheduling;

[Flags]
public enum CheckLayer
{
    None = 0,
    LocalMapping = 1,
    Tcp = 2,
    Share = 4,
    All = LocalMapping | Tcp | Share,
}
