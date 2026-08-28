namespace ScreenTranslator.History;

/// <summary>
/// One finished translation, as remembered for the "recent" list.
///
/// The captured image is deliberately not part of this: keeping thumbnails would turn a
/// small text file into a folder of screenshots that quietly grows forever, and the user
/// already has a separate, explicit setting for whether crops are kept on disk.
/// </summary>
public sealed class TranslationRecord
{
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>Display name of the language OCR settled on, e.g. "日文".</summary>
    public string SourceLanguage { get; set; } = "";

    /// <summary>What the OCR read.</summary>
    public string Original { get; set; } = "";

    /// <summary>What came back from the translator.</summary>
    public string Translation { get; set; } = "";

    /// <summary>A single line short enough for a menu item.</summary>
    public string Summary(int maxChars = 34)
    {
        var text = string.IsNullOrWhiteSpace(Translation) ? Original : Translation;

        // Newlines in a menu item render as boxes, and leading blank lines would make the
        // item look empty, so collapse everything to one line first.
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");

        if (text.Length == 0) return "(空)";
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}
