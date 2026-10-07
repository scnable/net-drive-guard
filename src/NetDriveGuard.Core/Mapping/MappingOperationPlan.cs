namespace NetDriveGuard.Core.Mapping;

public enum MappingOperationIntent
{
    EnsureMapped,
    EnsureUnmapped,
}

public enum MappingOperation
{
    None,
    Connect,
    Disconnect,
    Replace,
}

public enum MappingPlanStatus
{
    NoAction,
    Ready,
    Blocked,
}

public sealed record MappingOperationPlan
{
    internal MappingOperationPlan(
        MappingPlanStatus status,
        MappingOperation operation,
        string code,
        string? driveLetter,
        string? sharePath)
    {
        Status = status;
        Operation = operation;
        Code = code;
        DriveLetter = driveLetter;
        SharePath = sharePath;
    }

    public MappingPlanStatus Status { get; }

    public MappingOperation Operation { get; }

    public string Code { get; }

    public string? DriveLetter { get; }

    public string? SharePath { get; }

    public bool CanExecute => Status == MappingPlanStatus.Ready;
}
