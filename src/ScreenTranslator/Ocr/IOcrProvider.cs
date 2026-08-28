using System.Drawing;

namespace ScreenTranslator.Ocr;

/// <summary>
/// What to recognize and in which languages.
/// </summary>
/// <param name="SourceLanguage">A BCP-47 tag to pin the recognizer to, or "auto".</param>
/// <param name="Candidates">Languages to try when <paramref name="SourceLanguage"/> is "auto".</param>
public sealed record OcrRequest(string SourceLanguage, IReadOnlyList<string> Candidates)
{
    public const string Auto = "auto";

    public bool IsAuto => string.Equals(SourceLanguage, Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tags that should actually be attempted.</summary>
    public IReadOnlyList<string> ResolveTargets() =>
        IsAuto ? Candidates : new[] { SourceLanguage };
}

/// <summary>
/// Text recognition backend. Windows' built-in engine is the only implementation for now;
/// the interface exists because recognition quality is the biggest open question in this
/// project, and swapping in a local model (or a multimodal LLM) must not touch callers.
/// </summary>
public interface IOcrProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Recognizes text in <paramref name="image"/>. Never throws; failures come back as an outcome.</summary>
    Task<OcrOutcome> RecognizeAsync(Bitmap image, OcrRequest request, CancellationToken cancellationToken = default);
}
