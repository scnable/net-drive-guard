namespace NetDriveGuard.Core.Probing;

public sealed record ShareProbeOptions(
    string SharePath,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(5);

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(SharePath)
            || !SharePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("共享探测路径必须是 UNC 地址。", nameof(SharePath));
        }

        var segments = SharePath[2..].Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            throw new ArgumentException("共享探测路径必须包含服务器和共享名称。", nameof(SharePath));
        }

        if (EffectiveTimeout < TimeSpan.FromMilliseconds(100)
            || EffectiveTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout),
                "共享探测超时必须位于 100 毫秒到 30 秒之间。");
        }
    }
}
