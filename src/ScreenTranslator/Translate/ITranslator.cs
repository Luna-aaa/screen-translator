namespace ScreenTranslator.Translate;

/// <param name="Text">The recognized text to translate. Empty on the vision route.</param>
/// <param name="SourceLanguageTag">What the OCR engine thinks it read, or "" if unknown. A hint only.</param>
/// <param name="TargetLanguage">BCP-47 tag of the desired output language.</param>
/// <param name="ExtraPrompt">User-supplied style instruction, or empty.</param>
public sealed record TranslationRequest(
    string Text,
    string SourceLanguageTag,
    string TargetLanguage,
    string ExtraPrompt)
{
    /// <summary>
    /// The crop itself, for a translator that reads pictures. Null on the text route —
    /// which is every existing caller, so they keep the four-argument form unchanged.
    /// </summary>
    public System.Drawing.Bitmap? Image { get; init; }

    /// <summary>
    /// Several independent pieces of text to translate in one request, each of which must
    /// come back separately so it can be drawn where it was found. Null for the ordinary
    /// "translate this one thing" case.
    ///
    /// One request rather than one per block on purpose: a full screen is easily forty
    /// blocks, and forty round trips would be both slow and expensive. It also lets the
    /// model see the whole page, which is what stops a heading and its paragraph from
    /// being translated into two unrelated registers.
    /// </summary>
    public IReadOnlyList<string>? Segments { get; init; }

    /// <summary>
    /// Ask the vision model to append the text it read, so the popup has an "原文" to show.
    /// Ignored on the text route, which already has one.
    /// </summary>
    public bool WantOriginal { get; init; }

    /// <summary>
    /// This is the second attempt at segments the model handed back untouched, so the
    /// prompt says so out loud. Models comply much better when told which specific
    /// instruction they just ignored.
    /// </summary>
    public bool InsistOnTranslating { get; init; }
}

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

    /// <summary>
    /// Never throws; failures come back as an outcome with a readable message.
    /// </summary>
    /// <param name="onPartial">
    /// Receives the text so far, each time more of it arrives, so the popup can fill in
    /// as the model writes instead of after it finishes. Passing null asks for a single
    /// reply at the end — which is also what an engine that cannot stream will do
    /// regardless, so callers never have to ask whether streaming is supported.
    /// Always raised on the caller's thread via IProgress.
    /// </param>
    Task<TranslationOutcome> TranslateAsync(
        TranslationRequest request,
        IProgress<string>? onPartial = null,
        CancellationToken cancellationToken = default);

    /// <summary>Round-trips a tiny request so the settings screen can verify the setup.</summary>
    Task<TranslationOutcome> TestAsync(CancellationToken cancellationToken = default);
}
