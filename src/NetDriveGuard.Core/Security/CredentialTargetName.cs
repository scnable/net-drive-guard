using System.Security.Cryptography;
using System.Text;
using NetDriveGuard.Core.Mapping;

namespace NetDriveGuard.Core.Security;

public static class CredentialTargetName
{
    public static string ForDrive(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)
            || driveLetter.Length != 2
            || !char.IsAsciiLetter(driveLetter[0])
            || driveLetter[1] != ':')
        {
            throw new ArgumentException("盘符必须使用类似 Z: 的格式。", nameof(driveLetter));
        }

        return $"NetDriveGuard:Mapping:{char.ToUpperInvariant(driveLetter[0])}";
    }

    public static string ForMapping(string driveLetter, string sharePath)
    {
        var driveTarget = ForDrive(driveLetter);
        if (!NetworkSharePathComparer.TryNormalize(sharePath, out var normalizedShare))
        {
            throw new ArgumentException("共享地址必须包含服务器和共享名称。", nameof(sharePath));
        }

        var canonicalShare = normalizedShare.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalShare));
        return $"{driveTarget}:{Convert.ToHexString(digest.AsSpan(0, 16))}";
    }
}
