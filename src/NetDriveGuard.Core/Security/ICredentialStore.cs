namespace NetDriveGuard.Core.Security;

public interface ICredentialStore
{
    CredentialReadResult Read(string targetName);

    CredentialStoreOperationResult Write(
        string targetName,
        string userName,
        ReadOnlySpan<char> password);

    CredentialStoreOperationResult Delete(string targetName);
}
