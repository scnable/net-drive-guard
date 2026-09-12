using System.Globalization;
using System.Text.Json;

namespace NetDriveGuard.Core.Localization;

/// <summary>集中读取英文翻译。语言只在程序启动时设置，不改写已有日志。</summary>
public static class AppText
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadEnglish();

    public static string Language { get; set; } = "zh-CN";

    public static string Get(string text) => Translate(text, Language);

    public static string Translate(string text, string language) =>
        language == "en" && English.TryGetValue(text, out var translated) ? translated : text;

    public static string Format(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(format), arguments);

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(AppText).Assembly.GetManifestResourceStream(
            "NetDriveGuard.Core.Localization.English.json")
            ?? throw new InvalidOperationException("缺少内置英文翻译文件。");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("内置英文翻译文件无效。");
    }
}
