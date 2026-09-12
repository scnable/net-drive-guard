using System.Text.Json;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Events;

namespace NetDriveGuard.Windows;

/// <summary>统一切换配置和日志目录；复制时暂停日志写入，旧文件始终保留。</summary>
public sealed class ApplicationDataStore : IConfigurationStore, IMonitorEventStore, IDisposable
{
    private static readonly string[] DataFileNames =
        ["config.json", "events.jsonl", "events.1.jsonl", "events.2.jsonl", "events.3.jsonl"];
    private readonly string _defaultDirectory;
    private readonly string _locationFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonFileConfigurationStore _configuration;
    private JsonLinesMonitorEventStore _events;

    public ApplicationDataStore(string defaultDirectory)
    {
        _defaultDirectory = Path.GetFullPath(defaultDirectory);
        _locationFile = Path.Combine(_defaultDirectory, "storage-path.json");
        DataDirectory = File.Exists(_locationFile)
            ? NormalizeDirectory(JsonSerializer.Deserialize<string>(File.ReadAllText(_locationFile))
                ?? throw new IOException("保存目录记录为空。"))
            : _defaultDirectory;
        _configuration = new JsonFileConfigurationStore(Path.Combine(DataDirectory, "config.json"));
        _events = new JsonLinesMonitorEventStore(Path.Combine(DataDirectory, "events.jsonl"));
    }

    public string DataDirectory { get; private set; }

    public static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
            || directory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("请选择本机磁盘上的完整文件夹路径。");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType == DriveType.Network)
        {
            throw new ArgumentException("保存目录不能使用网络驱动器。");
        }

        return fullPath;
    }

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task<ConfigurationSaveResult> SaveAsync(
        MonitorConfiguration configuration, CancellationToken cancellationToken = default) =>
        SaveAsync(configuration, DataDirectory, cancellationToken);

    public async Task<ConfigurationSaveResult> SaveAsync(
        MonitorConfiguration configuration, string directory, CancellationToken cancellationToken = default)
    {
        if (!MonitorConfigurationValidator.Validate(configuration).IsValid)
        {
            return ConfigurationSaveResult.Failed("configuration.invalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = NormalizeDirectory(directory);
            if (string.Equals(destination, DataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return await _configuration.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
            }

            // 数据不能依赖正在监控、可能被取消的网络盘。
            if (destination.StartsWith(configuration.DriveLetter, StringComparison.OrdinalIgnoreCase))
            {
                return ConfigurationSaveResult.Failed("storage.monitored-drive");
            }

            if (DataFileNames.Any(name => Path.Exists(Path.Combine(destination, name))))
            {
                return ConfigurationSaveResult.Failed("storage.destination-conflict");
            }

            Directory.CreateDirectory(destination);
            foreach (var name in DataFileNames.Skip(1))
            {
                var source = Path.Combine(DataDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(destination, name), overwrite: false);
                }
            }

            // 新目录不覆盖任何已有配置。失败时保留已复制文件，当前运行仍使用旧目录。
            await using (var stream = new FileStream(Path.Combine(destination, "config.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(MonitorConfigurationJson.Serialize(configuration).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Directory.CreateDirectory(_defaultDirectory);
            var temporaryLocation = _locationFile + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryLocation, JsonSerializer.Serialize(destination), cancellationToken)
                .ConfigureAwait(false);
            // 最后更新路径记录；在此之前的失败不会改变下次启动使用的目录。
            File.Move(temporaryLocation, _locationFile, overwrite: true);
            _events.Dispose();
            DataDirectory = destination;
            _configuration = new JsonFileConfigurationStore(Path.Combine(destination, "config.json"));
            _events = new JsonLinesMonitorEventStore(Path.Combine(destination, "events.jsonl"));
            return ConfigurationSaveResult.Saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ConfigurationSaveResult.Failed("storage.directory-switch-failed");
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(MonitorEvent monitorEvent, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _events.AppendAsync(monitorEvent, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MonitorEvent>> LoadRecentAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _events.LoadRecentAsync(maxCount, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _events.Dispose();
        _gate.Dispose();
    }
}
