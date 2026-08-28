namespace ScreenTranslator.Ocr;

public enum OcrStatus
{
    Success,
    /// <summary>The engine ran but found nothing — usually an image with no text in it.</summary>
    NoText,
    /// <summary>Windows has no recognizer installed for any requested language.</summary>
    MissingLanguagePack,
    Failed,
}

public sealed class OcrOutcome
{
    public OcrStatus Status { get; private init; }

    public string Text { get; private init; } = "";

    /// <summary>Tag of the recognizer that produced <see cref="Text"/>.</summary>
    public string LanguageTag { get; private init; } = "";

    /// <summary>Populated for <see cref="OcrStatus.MissingLanguagePack"/>.</summary>
    public IReadOnlyList<OcrLanguage> MissingLanguages { get; private init; } = Array.Empty<OcrLanguage>();

    public string? Error { get; private init; }

    /// <summary>How long recognition took, for the log.</summary>
    public long ElapsedMs { get; private init; }

    /// <summary>Languages that were actually tried, for the log and the popup subtitle.</summary>
    public IReadOnlyList<string> TriedLanguages { get; private init; } = Array.Empty<string>();

    /// <summary>Copy of this outcome with timing/diagnostic detail attached.</summary>
    public OcrOutcome WithTelemetry(long elapsedMs, IReadOnlyList<string> tried) => new()
    {
        Status = Status,
        Text = Text,
        LanguageTag = LanguageTag,
        MissingLanguages = MissingLanguages,
        Error = Error,
        ElapsedMs = elapsedMs,
        TriedLanguages = tried,
    };

    public static OcrOutcome Success(string text, string languageTag) =>
        new() { Status = OcrStatus.Success, Text = text, LanguageTag = languageTag };

    public static OcrOutcome NoText(string languageTag) =>
        new() { Status = OcrStatus.NoText, LanguageTag = languageTag };

    public static OcrOutcome MissingLanguagePack(IReadOnlyList<OcrLanguage> missing) =>
        new() { Status = OcrStatus.MissingLanguagePack, MissingLanguages = missing };

    public static OcrOutcome Failed(string error) =>
        new() { Status = OcrStatus.Failed, Error = error };
}
