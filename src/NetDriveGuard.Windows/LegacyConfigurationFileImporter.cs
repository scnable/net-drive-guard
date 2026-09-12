using NetDriveGuard.Core.Migration;

namespace NetDriveGuard.Windows;

public sealed class LegacyConfigurationFileImporter : ILegacyConfigurationImporter
{
    private const long MaximumSourceBytes = 1024 * 1024;
    private readonly ILegacyTextFileReader _reader;

    public LegacyConfigurationFileImporter()
        : this(new LegacyTextFileReader())
    {
    }

    internal LegacyConfigurationFileImporter(ILegacyTextFileReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<LegacyConfigurationImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                Path.GetExtension(sourcePath),
                ".ps1",
                StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyConfigurationImportResult(false, "legacy.source-type-unsupported");
        }

        var source = await _reader.ReadSafeLinesAsync(
            Path.GetFullPath(sourcePath),
            MaximumSourceBytes,
            cancellationToken);
        if (!source.Success)
        {
            return new LegacyConfigurationImportResult(false, source.Code);
        }

        return LegacyConfigurationParser.Parse(source.Lines);
    }
}

internal sealed record LegacyTextReadResult(
    bool Success,
    string Code,
    IReadOnlyList<string> Lines)
{
    public static LegacyTextReadResult Failed(string code) => new(false, code, []);

    public static LegacyTextReadResult Loaded(IReadOnlyList<string> lines) =>
        new(true, "legacy.source-loaded", lines);
}

internal interface ILegacyTextFileReader
{
    Task<LegacyTextReadResult> ReadSafeLinesAsync(
        string sourcePath,
        long maximumBytes,
        CancellationToken cancellationToken);
}

internal sealed class LegacyTextFileReader : ILegacyTextFileReader
{
    public async Task<LegacyTextReadResult> ReadSafeLinesAsync(
        string sourcePath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return LegacyTextReadResult.Failed("legacy.source-not-found");
            }

            if (info.Length is <= 0 || info.Length > maximumBytes)
            {
                return LegacyTextReadResult.Failed("legacy.source-size-invalid");
            }

            var safeLines = new List<string>();
            await foreach (var line in File.ReadLinesAsync(sourcePath, cancellationToken))
            {
                if (safeLines.Count >= 2000)
                {
                    return LegacyTextReadResult.Failed("legacy.source-too-many-lines");
                }

                if (!IsSensitiveLine(line))
                {
                    safeLines.Add(line);
                }
            }

            return LegacyTextReadResult.Loaded(safeLines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return LegacyTextReadResult.Failed("legacy.source-read-failed");
        }
    }

    private static bool IsSensitiveLine(string line)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            line,
            @"(?i)\b(?:User|UserName|Pass|Password|Credential|Token|Secret)\s*=");
    }
}
