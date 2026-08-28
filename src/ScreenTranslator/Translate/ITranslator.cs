namespace ScreenTranslator.Translate;

/// <param name="Text">The recognized text to translate.</param>
/// <param name="SourceLanguageTag">What the OCR engine thinks it read, or "" if unknown. A hint only.</param>
/// <param name="TargetLanguage">BCP-47 tag of the desired output language.</param>
/// <param name="ExtraPrompt">User-supplied style instruction, or empty.</param>
public sealed record TranslationRequest(
    string Text,
    string SourceLanguageTag,
    string TargetLanguage,
    string ExtraPrompt);

/// <summary>
/// A translation backend. Implementations own their own settings and are recreated when
/// the configuration changes, so nothing here takes a config object.
/// </summary>
public interface ITranslator
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>False when required settings (address / key / model) are still blank.</summary>
    bool IsConfigured { get; }

    /// <summary>Never throws; failures come back as an outcome with a readable message.</summary>
    Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Round-trips a tiny request so the settings screen can verify the setup.</summary>
    Task<TranslationOutcome> TestAsync(CancellationToken cancellationToken = default);
}
