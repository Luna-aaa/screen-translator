namespace ScreenTranslator.Ocr;

/// <summary>
/// A language the Windows recognizer can be asked for.
/// </summary>
/// <param name="Tag">
/// BCP-47 tag as <c>OcrEngine.AvailableRecognizerLanguages</c> reports it.
/// </param>
/// <param name="CapabilityTag">
/// Tag used in the Windows optional-capability name, which is NOT always the same:
/// Simplified Chinese recognizes as <c>zh-Hans-CN</c> but installs as <c>zh-CN</c>.
/// </param>
public sealed record OcrLanguage(string Tag, string CapabilityTag, string DisplayName)
{
    /// <summary>Name accepted by <c>Add-WindowsCapability -Online -Name</c>.</summary>
    public string CapabilityName => $"Language.OCR~~~{CapabilityTag}~0.0.1.0";

    public override string ToString() => DisplayName;
}

public static class OcrLanguages
{
    public static readonly OcrLanguage English = new("en-US", "en-US", "英文");
    public static readonly OcrLanguage Japanese = new("ja-JP", "ja-JP", "日文");
    public static readonly OcrLanguage ChineseSimplified = new("zh-Hans-CN", "zh-CN", "中文（简体）");
    public static readonly OcrLanguage Korean = new("ko-KR", "ko-KR", "韩文");

    public static readonly IReadOnlyList<OcrLanguage> All =
        new[] { English, Japanese, ChineseSimplified, Korean };

    public static OcrLanguage? Find(string? tag) =>
        tag is null ? null : All.FirstOrDefault(l => TagsMatch(l.Tag, tag));

    public static string DisplayFor(string? tag) =>
        Find(tag)?.DisplayName ?? (string.IsNullOrWhiteSpace(tag) ? "未知" : tag!);

    /// <summary>
    /// Loose BCP-47 comparison. Windows reports recognizer tags at varying specificity
    /// ("zh-Hans" on one machine, "zh-Hans-CN" on another), so a prefix match either way
    /// is the only thing that holds up across systems.
    /// </summary>
    public static bool TagsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
               || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
}
