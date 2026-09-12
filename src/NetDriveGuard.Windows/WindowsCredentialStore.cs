using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NetDriveGuard.Core.Security;

namespace NetDriveGuard.Windows;

/// <summary>
/// 使用当前 Windows 用户的凭据管理器保存网络映射凭据。
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 5 * 512;
    private readonly IWindowsCredentialApi _api;

    public WindowsCredentialStore()
        : this(new WindowsCredentialApi())
    {
    }

    internal WindowsCredentialStore(IWindowsCredentialApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public CredentialReadResult Read(string targetName)
    {
        if (!IsValidTargetName(targetName))
        {
            return CredentialReadResult.Failed("credential.invalid-target");
        }

        var nativeResult = _api.Read(targetName);
        if (nativeResult.ErrorCode == ErrorNotFound)
        {
            return CredentialReadResult.NotFound();
        }

        if (nativeResult.ErrorCode != 0
            || string.IsNullOrWhiteSpace(nativeResult.UserName)
            || nativeResult.Password is null
            || nativeResult.Password.Length == 0)
        {
            nativeResult.ClearPassword();
            return CredentialReadResult.Failed(
                "credential.read-failed",
                nativeResult.ErrorCode == 0 ? null : nativeResult.ErrorCode);
        }

        try
        {
            return CredentialReadResult.Found(
                new NetworkCredentialSecret(nativeResult.UserName, nativeResult.Password));
        }
        finally
        {
            nativeResult.ClearPassword();
        }
    }

    public CredentialStoreOperationResult Write(
        string targetName,
        string userName,
        ReadOnlySpan<char> password)
    {
        if (!IsValidTargetName(targetName))
        {
            return CredentialStoreOperationResult.Failed("credential.invalid-target");
        }

        if (string.IsNullOrWhiteSpace(userName))
        {
            return CredentialStoreOperationResult.Failed("credential.invalid-username");
        }

        var blobSize = Encoding.Unicode.GetByteCount(password);
        if (password.IsEmpty
            || password.Contains('\0')
            || blobSize > MaximumCredentialBlobBytes)
        {
            return CredentialStoreOperationResult.Failed("credential.invalid-password");
        }

        var errorCode = _api.Write(targetName, userName, password);
        return errorCode == 0
            ? CredentialStoreOperationResult.Succeeded("credential.saved")
            : CredentialStoreOperationResult.Failed("credential.write-failed", errorCode);
    }

    public CredentialStoreOperationResult Delete(string targetName)
    {
        if (!IsValidTargetName(targetName))
        {
            return CredentialStoreOperationResult.Failed("credential.invalid-target");
        }

        var errorCode = _api.Delete(targetName);
        return errorCode is 0 or ErrorNotFound
            ? CredentialStoreOperationResult.Succeeded("credential.deleted")
            : CredentialStoreOperationResult.Failed("credential.delete-failed", errorCode);
    }

    private static bool IsValidTargetName(string targetName)
    {
        return !string.IsNullOrWhiteSpace(targetName)
            && targetName.Length <= 32767
            && !targetName.Contains('\0');
    }
}

internal interface IWindowsCredentialApi
{
    NativeCredentialReadResult Read(string targetName);

    int Write(string targetName, string userName, ReadOnlySpan<char> password);

    int Delete(string targetName);
}

internal sealed class NativeCredentialReadResult
{
    public NativeCredentialReadResult(int errorCode, string? userName = null, char[]? password = null)
    {
        ErrorCode = errorCode;
        UserName = userName;
        Password = password;
    }

    public int ErrorCode { get; }

    public string? UserName { get; }

    public char[]? Password { get; private set; }

    public void ClearPassword()
    {
        if (Password is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(
            MemoryMarshal.AsBytes(Password.AsSpan()));
        Password = null;
    }
}

internal sealed class WindowsCredentialApi : IWindowsCredentialApi
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const uint MaximumCredentialBlobBytes = 5 * 512;

    public NativeCredentialReadResult Read(string targetName)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            return new NativeCredentialReadResult(Marshal.GetLastPInvokeError());
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0
                || credential.CredentialBlobSize > MaximumCredentialBlobBytes
                || credential.CredentialBlobSize % sizeof(char) != 0)
            {
                return new NativeCredentialReadResult(13);
            }

            var characterCount = checked((int)credential.CredentialBlobSize / sizeof(char));
            var password = new char[characterCount];
            for (var index = 0; index < characterCount; index++)
            {
                password[index] = (char)Marshal.ReadInt16(
                    credential.CredentialBlob,
                    index * sizeof(char));
            }

            return new NativeCredentialReadResult(0, credential.UserName, password);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public int Write(string targetName, string userName, ReadOnlySpan<char> password)
    {
        var passwordBytes = new byte[Encoding.Unicode.GetByteCount(password)];
        Encoding.Unicode.GetBytes(password, passwordBytes);
        var handle = GCHandle.Alloc(passwordBytes, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)passwordBytes.Length),
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredentialPersistLocalMachine,
                UserName = userName,
            };

            return CredWrite(ref credential, 0)
                ? 0
                : Marshal.GetLastPInvokeError();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            handle.Free();
        }
    }

    public int Delete(string targetName)
    {
        return CredDelete(targetName, CredentialTypeGeneric, 0)
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string targetName,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
