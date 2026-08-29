namespace ScreenTranslator.Config;

/// <summary>A language the translations can come out in.</summary>
/// <param name="Tag">BCP-47, what goes in the config file.</param>
/// <param name="DisplayName">What the settings window shows.</param>
/// <param name="PromptName">What the model is told to translate into, in Chinese.</param>
/// <param name="ShortName">
/// What the result popup's header shows. Separate from <paramref name="DisplayName"/>
/// because that header also carries the source language, the timing and the token count,
/// and the popup is narrow enough to trim the end off — "中文（简体）" spent width that
/// the numbers after it needed.
/// </param>
public sealed record TargetLanguage(string Tag, string DisplayName, string PromptName, string ShortName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The languages translations can be produced in.
///
/// The list used to live inside the prompt builder as a switch expression, and the config
/// field it served had no UI at all — so the app translated into Simplified Chinese and
/// nothing else, despite the plumbing being complete end to end. Keeping the list here
/// means the settings dropdown and the prompt can never disagree about what "en" means.
/// </summary>
public static class TargetLanguages
{
    public static readonly IReadOnlyList<TargetLanguage> All = new[]
    {
        new TargetLanguage("zh-Hans", "中文（简体）", "简体中文", "中文"),
        new TargetLanguage("zh-Hant", "中文（繁體）", "繁体中文", "繁中"),
        new TargetLanguage("en", "English", "英文", "英文"),
        new TargetLanguage("ja", "日本語", "日文", "日文"),
        new TargetLanguage("ko", "한국어", "韩文", "韩文"),
    };

    public static TargetLanguage Default => All[0];

    /// <summary>Accepts the regional variants a hand-edited config might contain. Null when unrecognised.</summary>
    private static TargetLanguage? Match(string? tag) => tag switch
    {
        "zh-Hans" or "zh-Hans-CN" or "zh-CN" => All[0],
        "zh-Hant" or "zh-TW" or "zh-HK" => All[1],
        "en" or "en-US" or "en-GB" => All[2],
        "ja" or "ja-JP" => All[3],
        "ko" or "ko-KR" => All[4],
        _ => null,
    };

    /// <summary>For the settings dropdown, which must always land on something.</summary>
    public static TargetLanguage Find(string? tag) => Match(tag) ?? Default;

    public static bool IsKnown(string? tag) => Match(tag) is not null;

    /// <summary>
    /// How to name this language to the model. An unrecognised tag is passed through as
    /// written rather than silently becoming Chinese — someone who hand-edited the config
    /// to "de" wants German, and a model will understand the tag well enough.
    /// </summary>
    public static string Describe(string tag) => Match(tag)?.PromptName ?? tag;
}
