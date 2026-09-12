using System.Text;
using System.Text.Json;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Windows;

/// <summary>
/// 带大小上限的 JSONL 事件存储。轮转范围仅限同目录下固定编号的归档文件。
/// </summary>
public sealed class JsonLinesMonitorEventStore : IMonitorEventStore, IDisposable
{
    private const int DefaultMaxReadBytes = 1024 * 1024;
    private const int DefaultMaxEventFileBytes = 5 * 1024 * 1024;
    private const int DefaultArchiveCount = 3;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _eventPath;
    private readonly string _eventDirectory;
    private readonly int _maxReadBytes;
    private readonly int _maxEventFileBytes;
    private readonly int _archiveCount;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonLinesMonitorEventStore(
        string eventPath,
        int maxReadBytes = DefaultMaxReadBytes,
        int maxEventFileBytes = DefaultMaxEventFileBytes,
        int archiveCount = DefaultArchiveCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReadBytes, 128);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxReadBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEventFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEventFileBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(archiveCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(archiveCount, 10);

        _eventPath = Path.GetFullPath(eventPath);
        _eventDirectory = Path.GetDirectoryName(_eventPath)
            ?? throw new ArgumentException("事件路径必须包含目录。", nameof(eventPath));
        _maxReadBytes = maxReadBytes;
        _maxEventFileBytes = maxEventFileBytes;
        _archiveCount = archiveCount;
    }

    public async Task AppendAsync(
        MonitorEvent monitorEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitorEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var entry = StoredMonitorEvent.From(monitorEvent);
        var line = JsonSerializer.Serialize(entry, SerializerOptions);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_eventDirectory);
            var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            RotateIfNeeded(lineBytes);
            await using var stream = new FileStream(
                _eventPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<MonitorEvent>> LoadRecentAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 1000);
        cancellationToken.ThrowIfCancellationRequested();

        var events = new List<MonitorEvent>(maxCount);
        foreach (var path in EnumerateEventPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var text = await ReadTailAsync(path, cancellationToken).ConfigureAwait(false);
            var lines = text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = lines.Length - 1; index >= 0 && events.Count < maxCount; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var entry = JsonSerializer.Deserialize<StoredMonitorEvent>(
                        lines[index],
                        SerializerOptions);
                    if (entry is not null)
                    {
                        events.Add(entry.ToMonitorEvent());
                    }
                }
                catch (Exception exception) when (
                    exception is JsonException or ArgumentException)
                {
                    // 忽略损坏或不兼容行，继续恢复其他完整事件。
                }
            }

            if (events.Count >= maxCount)
            {
                break;
            }
        }

        return events;
    }

    public void Dispose()
    {
        _writeGate.Dispose();
    }

    private void RotateIfNeeded(int pendingBytes)
    {
        if (!File.Exists(_eventPath))
        {
            return;
        }

        var currentLength = new FileInfo(_eventPath).Length;
        if (currentLength + pendingBytes <= _maxEventFileBytes)
        {
            return;
        }

        foreach (var operation in EventRotationPlanner.Plan(_eventPath, _archiveCount))
        {
            if (File.Exists(operation.SourcePath))
            {
                File.Move(operation.SourcePath, operation.DestinationPath, overwrite: true);
            }
        }
    }

    private IEnumerable<string> EnumerateEventPaths()
    {
        yield return _eventPath;
        for (var index = 1; index <= _archiveCount; index++)
        {
            yield return EventRotationPlanner.GetArchivePath(_eventPath, index);
        }
    }

    private async Task<string> ReadTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var start = Math.Max(0, stream.Length - _maxReadBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var length = checked((int)(stream.Length - start));
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
        if (start == 0)
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n', StringComparison.Ordinal);
        return firstNewLine < 0 ? string.Empty : text[(firstNewLine + 1)..];
    }

    private sealed record StoredMonitorEvent(
        DateTimeOffset OccurredAt,
        MonitorState State,
        MonitorEventSeverity Severity,
        string Code,
        string Description)
    {
        public static StoredMonitorEvent From(MonitorEvent monitorEvent)
        {
            return new StoredMonitorEvent(
                monitorEvent.OccurredAt,
                monitorEvent.State,
                monitorEvent.Severity,
                monitorEvent.Code,
                monitorEvent.Description);
        }

        public MonitorEvent ToMonitorEvent()
        {
            return new MonitorEvent(OccurredAt, State, Severity, Code, Description);
        }
    }
}

internal sealed record EventRotationMove(string SourcePath, string DestinationPath);

internal static class EventRotationPlanner
{
    public static IReadOnlyList<EventRotationMove> Plan(string eventPath, int archiveCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(archiveCount, 1);

        var operations = new List<EventRotationMove>(archiveCount);
        for (var index = archiveCount - 1; index >= 1; index--)
        {
            operations.Add(
                new EventRotationMove(
                    GetArchivePath(eventPath, index),
                    GetArchivePath(eventPath, index + 1)));
        }

        operations.Add(new EventRotationMove(eventPath, GetArchivePath(eventPath, 1)));
        return operations;
    }

    public static string GetArchivePath(string eventPath, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        var fullPath = Path.GetFullPath(eventPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("事件路径必须包含目录。", nameof(eventPath));
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        return Path.Combine(directory, $"{name}.{index}{extension}");
    }
}
