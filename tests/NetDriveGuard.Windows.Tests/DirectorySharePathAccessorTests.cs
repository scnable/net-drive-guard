using NetDriveGuard.Core.Probing;
using NetDriveGuard.Windows;

namespace NetDriveGuard.Windows.Tests;

public sealed class DirectorySharePathAccessorTests
{
    [Fact]
    public void 不存在的本地测试路径被归类为未找到且不创建内容()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "share-accessor-missing",
            Guid.NewGuid().ToString("N"));
        var accessor = new DirectorySharePathAccessor();

        var result = accessor.Check(path);

        Assert.Equal(ShareProbeStatus.NotFound, result);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void 空路径被拒绝()
    {
        var accessor = new DirectorySharePathAccessor();
        Assert.Throws<ArgumentException>(() => accessor.Check(" "));
    }
}
