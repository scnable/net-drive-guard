namespace NetDriveGuard.Core.Security;

public sealed record CredentialStoreOperationResult(
    bool Success,
    string Code,
    int? NativeErrorCode = null)
{
    public static CredentialStoreOperationResult Succeeded(string code) => new(true, code);

    public static CredentialStoreOperationResult Failed(string code, int? nativeErrorCode = null) =>
        new(false, code, nativeErrorCode);
}
