using System.Text.Json;

namespace NetDriveGuard.Core.Diagnostics;

public static class MonitorHeartbeatJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(MonitorHeartbeat heartbeat)
    {
        Validate(heartbeat);
        return JsonSerializer.Serialize(
            new HeartbeatDocument(CurrentSchemaVersion, heartbeat),
            SerializerOptions);
    }

    public static MonitorHeartbeat? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HeartbeatDocument>(json, SerializerOptions);
            if (document?.SchemaVersion != CurrentSchemaVersion || document.Heartbeat is null)
            {
                return null;
            }

            Validate(document.Heartbeat);
            return document.Heartbeat;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static void Validate(MonitorHeartbeat? heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        if (string.IsNullOrWhiteSpace(heartbeat.ProductVersion)
            || heartbeat.ProductVersion.Length > 64)
        {
            throw new ArgumentException("心跳版本信息无效。", nameof(heartbeat));
        }

        if (!Enum.IsDefined(heartbeat.CurrentState)
            || !Enum.IsDefined(heartbeat.LastStableState)
            || heartbeat.ConsecutiveServerFailures < 0)
        {
            throw new ArgumentException("心跳状态信息无效。", nameof(heartbeat));
        }

        if (string.IsNullOrWhiteSpace(heartbeat.ResultCode)
            || heartbeat.ResultCode.Length > 64
            || heartbeat.ResultCode.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ArgumentException("心跳结果代码无效。", nameof(heartbeat));
        }
    }

    private sealed record HeartbeatDocument(
        int SchemaVersion,
        MonitorHeartbeat? Heartbeat);
}
