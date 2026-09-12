using System.Text;
using NetDriveGuard.Core.Diagnostics;

namespace NetDriveGuard.Windows;

public sealed class JsonFileMonitorHeartbeatStore : IMonitorHeartbeatStore
{
    private readonly string _heartbeatPath;
    private readonly IAtomicTextFileWriter _writer;

    public JsonFileMonitorHeartbeatStore(string heartbeatPath)
        : this(heartbeatPath, new AtomicTextFileWriter())
    {
    }

    internal JsonFileMonitorHeartbeatStore(
        string heartbeatPath,
        IAtomicTextFileWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heartbeatPath);
        _heartbeatPath = Path.GetFullPath(heartbeatPath);
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task WriteAsync(
        MonitorHeartbeat heartbeat,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = MonitorHeartbeatJson.Serialize(heartbeat);
        return _writer.WriteAsync(_heartbeatPath, json, cancellationToken);
    }
}

internal interface IAtomicTextFileWriter
{
    Task WriteAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken);
}

internal sealed class AtomicTextFileWriter : IAtomicTextFileWriter
{
    public async Task WriteAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("目标路径必须包含目录。", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = destinationPath + ".pending";
        await File.WriteAllTextAsync(
            temporaryPath,
            content,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }
}
