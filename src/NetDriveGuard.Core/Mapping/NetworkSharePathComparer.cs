namespace NetDriveGuard.Core.Mapping;

public static class NetworkSharePathComparer
{
    public static bool AreEquivalent(string? first, string? second)
    {
        return TryNormalize(first, out var normalizedFirst)
            && TryNormalize(second, out var normalizedSecond)
            && string.Equals(
                normalizedFirst,
                normalizedSecond,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = trimmed[2..].Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || segments.Any(segment => segment.Length == 0))
        {
            return false;
        }

        normalized = @"\\" + string.Join('\\', segments);
        return true;
    }
}
