namespace NetDriveGuard.Core.Mapping;

public sealed record MappingExecutionResult(bool Success, string Code, int? NativeErrorCode = null)
{
    public static MappingExecutionResult Succeeded(string code) => new(true, code);

    public static MappingExecutionResult Failed(string code, int? nativeErrorCode = null) =>
        new(false, code, nativeErrorCode);
}
