using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Windows;

namespace NetDriveGuard.Windows.Tests;

public sealed class JsonFileConfigurationStoreTests
{
    [Fact]
    public async Task 不存在的配置返回缺失且不创建目录或文件()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "read-only-missing-check",
            $"{Guid.NewGuid():N}.json");
        var store = new JsonFileConfigurationStore(path);

        var result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.Missing, result.Status);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task 无效配置在任何文件操作前被拒绝()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "invalid-save-check",
            $"{Guid.NewGuid():N}.json");
        var store = new JsonFileConfigurationStore(path);
        var invalid = new MonitorConfiguration("bad", "not-a-share");

        var result = await store.SaveAsync(invalid);

        Assert.False(result.Success);
        Assert.Equal("configuration.invalid", result.Code);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void 空配置路径被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileConfigurationStore(" "));
    }

    [Fact]
    public async Task 首次保存后能够重新加载且不包含密码字段()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "TestResults",
            "configuration-first-save",
            Guid.NewGuid().ToString("N"),
            "config.json");
        var store = new JsonFileConfigurationStore(path);
        var configuration = new MonitorConfiguration("Z:", @"\\server\share");

        var save = await store.SaveAsync(configuration);
        var load = await store.LoadAsync();
        var json = await File.ReadAllTextAsync(path);

        Assert.True(save.Success);
        Assert.True(load.IsLoaded);
        Assert.Equal(configuration, load.Configuration);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 保存前取消不会创建目录或文件()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "cancelled-save-check",
            $"{Guid.NewGuid():N}.json");
        var store = new JsonFileConfigurationStore(path);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(
                new MonitorConfiguration("Z:", @"\\server\share"),
                source.Token));
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
