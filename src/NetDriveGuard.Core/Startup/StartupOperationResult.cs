namespace NetDriveGuard.Core.Startup;

public sealed record StartupOperationResult(bool Success, string Code)
{
    public static StartupOperationResult Succeeded(string code) => new(true, code);

    public static StartupOperationResult Failed(string code) => new(false, code);
}
