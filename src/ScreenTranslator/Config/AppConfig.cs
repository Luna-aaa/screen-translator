namespace ScreenTranslator.Config;

/// <summary>
/// Everything the user can change. Serialized to %APPDATA%\ScreenTranslator\config.json.
/// Secrets live only in *Protected fields (DPAPI ciphertext) — never in plaintext.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Bumped when the shape changes in a way that needs migration.
    /// v2: candidate OCR languages are seeded from what the machine can actually recognize.
    /// v3: timeout / streaming / theme / history / capture folder moved out of the
    ///     top level and into each route, which now owns its own copy.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Defaults to 1, NOT the current version. System.Text.Json leaves a property at its
    /// initializer when the JSON has no such field, so defaulting to the current version
    /// would make every pre-existing config claim to be already migrated and silently skip
    /// the migration that exists for exactly those files.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    // ---- general ----------------------------------------------------------
    /// <summary>框选翻译 (Ctrl+Alt+Q).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Q";

    /// <summary>
    /// 全屏翻译 (Ctrl+Alt+W). Empty means "do not register it" — a global hotkey is a
    /// scarce, shared resource, and someone who does not use this feature should not be
    /// holding one hostage. The tray menu entry works either way.
    /// </summary>
    public string OverlayHotkey { get; set; } = "Ctrl+Alt+W";

    public bool AutoStart { get; set; }

    // ---- OCR --------------------------------------------------------------
    /// <summary>"auto", or a BCP-47 tag to pin the recognizer to one language.</summary>
    public string OcrSourceLanguage { get; set; } = "auto";

    /// <summary>When OcrSourceLanguage is "auto", only these engines are tried.</summary>
    public List<string> OcrCandidateLanguages { get; set; } = new() { "en-US", "ja-JP" };

    // ---- routes -----------------------------------------------------------
    /// <summary>Id of the active <c>ITranslator</c> implementation.</summary>
    public string ActiveTranslator { get; set; } = OpenAiSettings.TranslatorId;

    /// <summary>
    /// Which route 框选翻译 takes: <see cref="Pipelines.Classic"/> (OCR then translate) or
    /// <see cref="Pipelines.Vision"/> (send the picture itself). Does not affect 全屏翻译,
    /// which always needs OCR for coordinates.
    /// </summary>
    public string Pipeline { get; set; } = Pipelines.Classic;

    /// <summary>识文翻译.</summary>
    public OpenAiSettings OpenAi { get; set; } = new();

    /// <summary>看图翻译.</summary>
    public VisionSettings Vision { get; set; } = new();

    /// <summary>全屏翻译.</summary>
    public SnapshotSettings Snapshot { get; set; } = new();

    /// <summary>Target language for every route. Fixed to Simplified Chinese for v1.</summary>
    public string TargetLanguage { get; set; } = "zh-Hans";

    /// <summary>
    /// Whichever of the two 框选翻译 routes is currently selected. JsonIgnore because it is
    /// a view of OpenAi/Vision, not a field: serializing it wrote a third copy of one of
    /// them into config.json, where it looked like a real setting nobody could edit.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PopupRouteSettings ActiveRoute => Pipelines.IsVision(Pipeline) ? Vision : OpenAi;

    // ---- legacy ------------------------------------------------------------
    // Read only to migrate a pre-v3 file into the per-route fields above; nothing consults
    // them afterwards. They stay declared so an old config still parses.

    public int RequestTimeoutSeconds { get; set; } = 30;
    public bool StreamTranslation { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public string PopupTheme { get; set; } = "dark";
    public bool SaveCaptures { get; set; } = true;
    public string CaptureDirectory { get; set; } = "";

    public AppConfig Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Hotkey = Hotkey,
        OverlayHotkey = OverlayHotkey,
        AutoStart = AutoStart,
        OcrSourceLanguage = OcrSourceLanguage,
        OcrCandidateLanguages = new List<string>(OcrCandidateLanguages),
        ActiveTranslator = ActiveTranslator,
        Pipeline = Pipeline,
        OpenAi = OpenAi.Clone(),
        Vision = Vision.Clone(),
        Snapshot = Snapshot.Clone(),
        TargetLanguage = TargetLanguage,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        StreamTranslation = StreamTranslation,
        KeepHistory = KeepHistory,
        PopupTheme = PopupTheme,
        SaveCaptures = SaveCaptures,
        CaptureDirectory = CaptureDirectory,
    };
}
