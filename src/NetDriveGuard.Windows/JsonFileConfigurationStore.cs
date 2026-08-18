using System.Text;
using NetDriveGuard.Core.Configuration;

namespace NetDriveGuard.Windows;

/// <summary>
/// 使用同目录临时文件和原子替换保存非敏感 JSON 配置。
/// </summary>
public sealed class JsonFileConfigurationStore : IConfigurationStore
{
    private readonly string _configurationPath;
    private readonly string _configurationDirectory;

    public JsonFileConfigurationStore(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _configurationPath = Path.GetFullPath(configurationPath);
        _configurationDirectory = Path.GetDirectoryName(_configurationPath)
            ?? throw new ArgumentException("配置路径必须包含目录。", nameof(configurationPath));
    }

    public async Task<ConfigurationLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_configurationPath))
        {
            return new ConfigurationLoadResult(ConfigurationLoadStatus.Missing);
        }

        try
        {
            var json = await File.ReadAllTextAsync(
                _configurationPath,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            return MonitorConfigurationJson.Deserialize(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ConfigurationLoadResult(
                ConfigurationLoadStatus.StorageError,
                Issues:
                [
                    new ConfigurationIssue(
                        "configuration.read-failed",
                        "无法读取配置文件。"),
                ]);
        }
    }

    public async Task<ConfigurationSaveResult> SaveAsync(
        MonitorConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = MonitorConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            return ConfigurationSaveResult.Failed("configuration.invalid");
        }

        var temporaryPath = Path.Combine(
            _configurationDirectory,
            $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(_configurationDirectory);
            var json = MonitorConfigurationJson.Serialize(configuration);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_configurationPath))
            {
                File.Replace(temporaryPath, _configurationPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _configurationPath);
            }

            return ConfigurationSaveResult.Saved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryRemoveTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryRemoveTemporaryFile(temporaryPath);
            return ConfigurationSaveResult.Failed("configuration.write-failed");
        }
    }

    private static void TryRemoveTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不覆盖原始保存错误；后续诊断只记录固定错误代码。
        }
    }
}
