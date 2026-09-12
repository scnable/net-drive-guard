using System.Text.Json;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.Core.Diagnostics;

public static class DiagnosticReportJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(
        string productVersion,
        DateTimeOffset generatedAt,
        MonitorConfiguration? configuration,
        MonitorSnapshot snapshot,
        IReadOnlyList<MonitorEvent> recentEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recentEvents);

        var document = new DiagnosticDocument(
            CurrentSchemaVersion,
            new ProductSection("NetDriveGuard", productVersion),
            generatedAt,
            CreateConfigurationSection(configuration),
            new SnapshotSection(
                snapshot.CurrentState,
                snapshot.LastStableState,
                snapshot.ConsecutiveServerFailures,
                snapshot.LastTransitionAt,
                snapshot.LastObservationAt),
            recentEvents
                .Take(100)
                .Select(monitorEvent => new EventSection(
                    monitorEvent.OccurredAt,
                    monitorEvent.State,
                    monitorEvent.Severity,
                    monitorEvent.Code))
                .ToArray());
        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    private static ConfigurationSection CreateConfigurationSection(
        MonitorConfiguration? configuration)
    {
        if (configuration is null)
        {
            return new ConfigurationSection(false, null, null, false, false, false);
        }

        return new ConfigurationSection(
            true,
            configuration.DriveLetter,
            configuration.Profile.ToString(),
            configuration.RepairMissingMapping,
            configuration.RemoveMappingWhenOffline,
            configuration.StartAtLogon);
    }

    private sealed record DiagnosticDocument(
        int SchemaVersion,
        ProductSection Product,
        DateTimeOffset GeneratedAt,
        ConfigurationSection Configuration,
        SnapshotSection Snapshot,
        IReadOnlyList<EventSection> RecentEvents);

    private sealed record ProductSection(string Name, string Version);

    private sealed record ConfigurationSection(
        bool IsConfigured,
        string? DriveLetter,
        string? Profile,
        bool RepairMissingMapping,
        bool RemoveMappingWhenOffline,
        bool StartAtLogon);

    private sealed record SnapshotSection(
        MonitorState CurrentState,
        MonitorState LastStableState,
        int ConsecutiveServerFailures,
        DateTimeOffset LastTransitionAt,
        DateTimeOffset LastObservationAt);

    private sealed record EventSection(
        DateTimeOffset OccurredAt,
        MonitorState State,
        MonitorEventSeverity Severity,
        string Code);
}
