namespace NetDriveGuard.Core.Probing;

public sealed record TcpProbeOptions(
    string Host,
    int Port = 445,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMilliseconds(800);

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("探测主机不能为空。", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "端口必须位于 1 到 65535 之间。");
        }

        if (EffectiveTimeout < TimeSpan.FromMilliseconds(50)
            || EffectiveTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "探测超时必须位于 50 毫秒到 30 秒之间。");
        }
    }
}
