namespace NetDriveGuard.Core.Security;

public enum CredentialReadStatus
{
    Found,
    NotFound,
    Error,
}

public sealed class CredentialReadResult : IDisposable
{
    private CredentialReadResult(
        CredentialReadStatus status,
        string code,
        NetworkCredentialSecret? credential,
        int? nativeErrorCode)
    {
        Status = status;
        Code = code;
        Credential = credential;
        NativeErrorCode = nativeErrorCode;
    }

    public CredentialReadStatus Status { get; }

    public string Code { get; }

    public NetworkCredentialSecret? Credential { get; }

    public int? NativeErrorCode { get; }

    public static CredentialReadResult Found(NetworkCredentialSecret credential) =>
        new(
            CredentialReadStatus.Found,
            "credential.found",
            credential ?? throw new ArgumentNullException(nameof(credential)),
            null);

    public static CredentialReadResult NotFound() =>
        new(CredentialReadStatus.NotFound, "credential.not-found", null, null);

    public static CredentialReadResult Failed(string code, int? nativeErrorCode = null) =>
        new(CredentialReadStatus.Error, code, null, nativeErrorCode);

    public void Dispose()
    {
        Credential?.Dispose();
    }
}
