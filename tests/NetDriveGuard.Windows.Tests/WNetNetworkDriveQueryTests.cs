using NetDriveGuard.Core.Mapping;

namespace NetDriveGuard.Windows.Tests;

public class WNetNetworkDriveQueryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Z")]
    [InlineData("1:")]
    [InlineData("ZZ:")]
    public void 非法盘符不调用系统映射查询(string driveLetter)
    {
        var query = new WNetNetworkDriveQuery();

        var result = query.Query(driveLetter);

        Assert.Equal(NetworkDriveQueryStatus.InvalidDrive, result.Status);
        Assert.False(result.IsMapped);
    }

    [Fact]
    public void 本地磁盘不会被误认为网络映射()
    {
        var query = new WNetNetworkDriveQuery();

        var result = query.Query("D:");

        Assert.Equal(NetworkDriveQueryStatus.NotMapped, result.Status);
        Assert.False(result.IsMapped);
        Assert.Null(result.RemotePath);
    }
}
