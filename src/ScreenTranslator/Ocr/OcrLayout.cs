using System.Drawing;

namespace ScreenTranslator.Ocr;

/// <summary>One recognized line, and where it sits in the source bitmap.</summary>
/// <param name="Text">What the engine read. May well be wrong — the position is what this is for.</param>
/// <param name="Bounds">In source-bitmap pixels, already mapped back through any scaling the engine needed.</param>
public sealed record OcrLineBox(string Text, RectangleF Bounds);

/// <summary>
/// Recognition that keeps the geometry.
///
/// The ordinary <see cref="OcrOutcome"/> throws the positions away, because the popup only
/// ever needed the text. Drawing translations back over the original words needs to know
/// where those words were, so this is a second, parallel result shape rather than a change
/// to the one every existing caller already relies on.
/// </summary>
public sealed class OcrLayout
{
    public OcrStatus Status { get; private init; }

    public IReadOnlyList<OcrLineBox> Lines { get; private init; } = Array.Empty<OcrLineBox>();

    /// <summary>Tag of the recognizer whose lines these are.</summary>
    public string LanguageTag { get; private init; } = "";

    public IReadOnlyList<OcrLanguage> MissingLanguages { get; private init; } = Array.Empty<OcrLanguage>();

    public string? Error { get; private init; }

    public long ElapsedMs { get; private init; }

    public bool IsSuccess => Status == OcrStatus.Success;

    public static OcrLayout Success(IReadOnlyList<OcrLineBox> lines, string languageTag, long elapsedMs) => new()
    {
        Status = OcrStatus.Success,
        Lines = lines,
        LanguageTag = languageTag,
        ElapsedMs = elapsedMs,
    };

    public static OcrLayout NoText(long elapsedMs) => new() { Status = OcrStatus.NoText, ElapsedMs = elapsedMs };

    public static OcrLayout MissingLanguagePack(IReadOnlyList<OcrLanguage> missing) =>
        new() { Status = OcrStatus.MissingLanguagePack, MissingLanguages = missing };

    public static OcrLayout Failed(string error) => new() { Status = OcrStatus.Failed, Error = error };
}

/// <summary>
/// Recognition that reports where the text is. Kept separate from <see cref="IOcrProvider"/>
/// so that adding it does not oblige every future recognizer to produce geometry — a
/// multimodal model, for instance, reads text well and cannot give reliable coordinates
/// at all.
/// </summary>
public interface IOcrLayoutProvider
{
    /// <summary>Never throws except on cancellation; failures come back as an outcome.</summary>
    Task<OcrLayout> RecognizeLayoutAsync(
        Bitmap image, OcrRequest request, CancellationToken cancellationToken = default);
}
