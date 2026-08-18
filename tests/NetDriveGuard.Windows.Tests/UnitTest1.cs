namespace NetDriveGuard.Windows.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void 同一应用标识只允许一个主实例()
    {
        var applicationId = $"NetDriveGuard.Tests.{Guid.NewGuid():N}";

        using var primary = new NamedMutexSingleInstanceGuard(applicationId);
        using var secondary = new NamedMutexSingleInstanceGuard(applicationId);

        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
    }

    [Fact]
    public void 主实例释放后新实例能够取得所有权()
    {
        var applicationId = $"NetDriveGuard.Tests.{Guid.NewGuid():N}";
        var primary = new NamedMutexSingleInstanceGuard(applicationId);

        Assert.True(primary.IsPrimaryInstance);
        primary.Dispose();

        using var replacement = new NamedMutexSingleInstanceGuard(applicationId);
        Assert.True(replacement.IsPrimaryInstance);
    }

    [Fact]
    public void 空应用标识被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new NamedMutexSingleInstanceGuard(" "));
    }
}
