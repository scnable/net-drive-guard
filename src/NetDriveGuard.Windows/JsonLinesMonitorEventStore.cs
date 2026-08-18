using System.Text;
using System.Text.Json;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Windows;

/// <summary>
/// 只追加的 JSONL 事件存储。读取时只扫描文件尾部，不执行删除、覆盖或轮转。
/// </summary>
public sealed class JsonLinesMonitorEventStore : IMonitorEventStore, IDisposable
{
    private const int DefaultMaxReadBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _eventPath;
    private readonly string _eventDirectory;
    private readonly int _maxReadBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonLinesMonitorEventStore(
        string eventPath,
        int maxReadBytes = DefaultMaxReadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReadBytes, 128);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxReadBytes, 16 * 1024 * 1024);

        _eventPath = Path.GetFullPath(eventPath);
        _eventDirectory = Path.GetDirectoryName(_eventPath)
            ?? throw new ArgumentException("事件路径必须包含目录。", nameof(eventPath));
        _maxReadBytes = maxReadBytes;
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

        if (!File.Exists(_eventPath))
        {
            return [];
        }

        var text = await ReadTailAsync(cancellationToken).ConfigureAwait(false);
        var lines = text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var events = new List<MonitorEvent>(Math.Min(maxCount, lines.Length));
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

        return events;
    }

    public void Dispose()
    {
        _writeGate.Dispose();
    }

    private async Task<string> ReadTailAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _eventPath,
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
