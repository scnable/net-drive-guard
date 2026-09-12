using System.Security.Cryptography;

namespace NetDriveGuard.Core.Security;

/// <summary>
/// 在托管内存中短暂持有密码，并在释放时主动清零。
/// </summary>
public sealed class NetworkCredentialSecret : IDisposable
{
    private char[]? _password;

    public NetworkCredentialSecret(string userName, ReadOnlySpan<char> password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("用户名不能为空。", nameof(userName));
        }

        if (password.IsEmpty || password.Contains('\0'))
        {
            throw new ArgumentException("密码不能为空且不能包含空字符。", nameof(password));
        }

        UserName = userName;
        _password = password.ToArray();
    }

    public string UserName { get; }

    public ReadOnlySpan<char> Password
    {
        get
        {
            ObjectDisposedException.ThrowIf(_password is null, this);
            return _password;
        }
    }

    public void Dispose()
    {
        if (_password is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(_password.AsSpan()));
        _password = null;
    }
}
