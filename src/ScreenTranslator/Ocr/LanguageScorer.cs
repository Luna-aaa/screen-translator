using System.Globalization;

namespace ScreenTranslator.Ocr;

/// <summary>
/// Picks which recognizer's output to believe when the source language is "auto".
///
/// Windows OCR will not tell you what language it saw, and it has no confidence score —
/// it just returns whatever the engine you picked made of the pixels. So we run every
/// candidate engine and judge the results by how well the characters they produced fit
/// the language that produced them. A Japanese engine pointed at English text emits a
/// short soup of unrelated kanji; an English engine pointed at Japanese emits Latin
/// gibberish. Both score badly, and the right engine wins.
///
/// This heuristic is the most likely thing in the OCR path to need replacing (short
/// strings and mixed Japanese/English are its weak spots), which is why it is one small
/// isolated function.
/// </summary>
internal static class LanguageScorer
{
    public static double Score(string text, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int latin = 0, digit = 0, kana = 0, cjk = 0, hangul = 0, noise = 0;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;

            if (IsKana(c)) kana++;
            else if (IsHangul(c)) hangul++;
            else if (IsCjkIdeograph(c)) cjk++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            else if (char.IsDigit(c)) digit++;
            else if (IsNeutralPunctuation(c)) { /* neutral */ }
            else noise++;
        }

        var meaningful = latin + digit + kana + cjk + hangul;
        if (meaningful == 0) return 0;

        double Ratio(int n) => (double)n / meaningful;

        // Kana that sit in runs, not scattered singletons. A Chinese glyph misread as kana
        // shows up alone between CJK characters; genuine Japanese comes in stretches of
        // particles and okurigana. Counting runs separates the two far better than a raw
        // kana tally does.
        var (kanaRunCount, kanaInRuns) = MeasureKanaRuns(text);
        var kanaRunRatio = (double)kanaInRuns / meaningful;
        var hangulRatio = Ratio(hangul);

        var fit = languageTag switch
        {
            _ when OcrLanguages.TagsMatch(languageTag, OcrLanguages.English.Tag) =>
                Ratio(latin + digit),

            // Running Japanese over Chinese text does not produce zero kana - it produces
            // a few, where a Chinese glyph happened to resemble one. Real Japanese prose
            // is roughly 40-60% kana, so the bonus is gated on a proportion rather than
            // mere presence; otherwise a handful of stray kana lets the Japanese engine
            // outscore the Chinese one on Chinese text.
            _ when OcrLanguages.TagsMatch(languageTag, OcrLanguages.Japanese.Tag) =>
                Ratio(kana + cjk + latin + digit) * KanaConfidence(kanaRunRatio, kanaRunCount),

            // Conversely, a real amount of kana in a Chinese result means Japanese text
            // was misread. A trace is expected noise and must not trigger the penalty.
            _ when OcrLanguages.TagsMatch(languageTag, OcrLanguages.ChineseSimplified.Tag) =>
                Ratio(cjk + latin + digit) * (kanaRunRatio > 0.2 ? 0.4 : 1.0),

            _ when OcrLanguages.TagsMatch(languageTag, OcrLanguages.Korean.Tag) =>
                Ratio(hangul + cjk + latin + digit) * (hangulRatio > 0.15 ? 1.3 : 0.5),

            _ => Ratio(meaningful),
        };

        fit = Math.Clamp(fit, 0, 1.3);

        // Longer output is better evidence, but garbage characters actively count against it.
        return meaningful * fit - noise * 0.5;
    }

    /// <summary>
    /// How strongly the kana argue that this really is Japanese.
    ///
    /// Japanese prose runs roughly 35-70% kana spread over many runs. One stray run in an
    /// otherwise Chinese sentence — a single 你 misread as two kana, say — lands near 10%
    /// with a single run, and must not be enough to outrank the Chinese engine.
    /// </summary>
    private static double KanaConfidence(double kanaRunRatio, int kanaRunCount)
    {
        if (kanaRunRatio >= 0.25 && kanaRunCount >= 2) return 1.35;
        if (kanaRunRatio >= 0.12) return 1.0;
        if (kanaRunRatio >= 0.04) return 0.6;
        return 0.45;
    }

    /// <summary>Counts kana stretches of two or more; isolated kana are ignored entirely.</summary>
    private static (int RunCount, int CharsInRuns) MeasureKanaRuns(string text)
    {
        int runCount = 0, charsInRuns = 0, current = 0;

        foreach (var c in text)
        {
            if (IsKana(c)) { current++; continue; }
            if (current >= 2) { runCount++; charsInRuns += current; }
            current = 0;
        }
        if (current >= 2) { runCount++; charsInRuns += current; }

        return (runCount, charsInRuns);
    }

    public static bool IsCjkLike(char c) => IsKana(c) || IsCjkIdeograph(c) || IsHangul(c) || IsCjkPunctuation(c);

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ';

    private static bool IsCjkIdeograph(char c) =>
        c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿';

    private static bool IsHangul(char c) =>
        c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static bool IsCjkPunctuation(char c) =>
        c is >= '　' and <= '〿' or >= '＀' and <= '￯';

    private static bool IsNeutralPunctuation(char c) =>
        char.IsPunctuation(c) || char.IsSymbol(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherPunctuation;
}
