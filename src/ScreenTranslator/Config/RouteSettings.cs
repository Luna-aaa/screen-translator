using System.Text.Json.Serialization;

namespace ScreenTranslator.Config;

/// <summary>
/// The four things any OpenAI-compatible chat endpoint needs.
/// </summary>
public interface IChatServiceSettings
{
    string BaseUrl { get; }
    string Model { get; }
    string ApiKeyProtected { get; }
    string ExtraPrompt { get; }
}

/// <summary>
/// Everything one translation route owns.
///
/// There are three of them — 识文翻译, 看图翻译 and 全屏翻译 — and each keeps its own copy
/// of all of this. Sharing was the obvious first design and it is the wrong one: the routes
/// point at different vendors, answer at very different speeds, and produce different kinds
/// of file, so a single "timeout" or "screenshot folder" would always be wrong for two of
/// the three. Independent also means switching between them never asks the user to re-enter
/// anything.
/// </summary>
public abstract class RouteSettings : IChatServiceSettings
{
    /// <summary>Id of the <see cref="ServicePreset"/> the user picked, or "custom".</summary>
    public string Preset { get; set; } = "custom";

    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>DPAPI ciphertext, base64. Read/write it through <see cref="SecureStore"/>.</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>Extra instruction appended to this route's prompt. Empty = engine default.</summary>
    public string ExtraPrompt { get; set; } = "";

    /// <summary>How long to wait for the service. Per route: the picture routes are far slower.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Longest edge of the image actually sent, in pixels. Unused by the text route, which
    /// sends no image; kept on the base so one translator constructor serves all three.
    /// </summary>
    public int MaxImageEdge { get; set; } = 1600;

    /// <summary>Keep a PNG of what this route worked on.</summary>
    public bool SaveCaptures { get; set; } = true;

    /// <summary>Folder for those PNGs. Empty means this route's default.</summary>
    public string CaptureDirectory { get; set; } = "";

    [JsonIgnore]
    public bool HasKey => !string.IsNullOrEmpty(ApiKeyProtected);

    /// <summary>True once address, model and key are all filled in.</summary>
    [JsonIgnore]
    public bool LooksConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model) && HasKey;

    protected void CopyBaseInto(RouteSettings target)
    {
        target.Preset = Preset;
        target.BaseUrl = BaseUrl;
        target.Model = Model;
        target.ApiKeyProtected = ApiKeyProtected;
        target.ExtraPrompt = ExtraPrompt;
        target.TimeoutSeconds = TimeoutSeconds;
        target.MaxImageEdge = MaxImageEdge;
        target.SaveCaptures = SaveCaptures;
        target.CaptureDirectory = CaptureDirectory;
    }
}

/// <summary>
/// A route whose result lands in the little popup, so it also owns how that popup behaves.
/// The whole-screen route has no popup and therefore none of this.
/// </summary>
public abstract class PopupRouteSettings : RouteSettings
{
    /// <summary>
    /// Show the translation as it is written instead of after it finishes. Off is a real
    /// fallback, not just a preference: a few compatible services mishandle stream:true.
    /// </summary>
    public bool StreamTranslation { get; set; } = true;

    /// <summary>Colour scheme id for the result popup; see <c>PopupThemes</c>.</summary>
    public string PopupTheme { get; set; } = "dark";

    /// <summary>
    /// Remember this route's translations in history.json. Off also means nothing new is
    /// written; clearing what is already there is a separate, explicit action.
    /// </summary>
    public bool KeepHistory { get; set; } = true;

    protected void CopyPopupInto(PopupRouteSettings target)
    {
        CopyBaseInto(target);
        target.StreamTranslation = StreamTranslation;
        target.PopupTheme = PopupTheme;
        target.KeepHistory = KeepHistory;
    }
}

/// <summary>
/// 识文翻译 — Windows OCR reads the crop, then a text model translates it. The original
/// route, and still the default.
/// </summary>
public sealed class OpenAiSettings : PopupRouteSettings
{
    public const string TranslatorId = "openai-compatible";

    public OpenAiSettings()
    {
        Preset = "deepseek";
        BaseUrl = "https://api.deepseek.com/v1";
        Model = "deepseek-chat";
    }

    public OpenAiSettings Clone()
    {
        var copy = new OpenAiSettings();
        CopyPopupInto(copy);
        return copy;
    }
}

/// <summary>
/// 看图翻译 — the crop itself goes to a model that can see, which reads and translates in
/// one step. Skips OCR entirely, so misread characters and language guessing cannot happen.
/// </summary>
public sealed class VisionSettings : PopupRouteSettings
{
    public VisionSettings()
    {
        Preset = "qwen-vl";
        BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        Model = "qwen-vl-max-latest";
    }

    /// <summary>
    /// Have the model send back the text it read, so 显示原文 / 复制原文 work on this route
    /// too. There is no OCR step here to produce one otherwise. Costs a few extra output
    /// tokens per translation, which is why it can be switched off.
    /// </summary>
    public bool IncludeOriginal { get; set; } = true;

    public VisionSettings Clone()
    {
        var copy = new VisionSettings();
        CopyPopupInto(copy);
        copy.IncludeOriginal = IncludeOriginal;
        return copy;
    }
}

/// <summary>
/// 全屏翻译 — OCR supplies the coordinates, a model that can see supplies the meaning, and
/// the translations are drawn back over the words they came from.
///
/// Its own service rather than a share of <see cref="VisionSettings"/>, because the two get
/// used very differently: framing one line is worth a slow accurate model, while re-typing
/// a whole screen is worth a fast cheap one.
/// </summary>
public sealed class SnapshotSettings : RouteSettings
{
    public SnapshotSettings()
    {
        Preset = "qwen-vl";
        BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        Model = "qwen-vl-max-latest";
        TimeoutSeconds = 90;      // a whole screen is dozens of blocks in one request
        MaxImageEdge = 2000;      // small on-screen type has to survive the downscale
    }

    public SnapshotSettings Clone()
    {
        var copy = new SnapshotSettings();
        CopyBaseInto(copy);
        return copy;
    }
}

/// <summary>
/// Which route a capture takes. Stored as a string rather than an enum so an unknown value
/// in a hand-edited config falls back to the classic route instead of throwing.
/// </summary>
public static class Pipelines
{
    /// <summary>Windows OCR reads the crop, then a text model translates it. The default.</summary>
    public const string Classic = "classic";

    /// <summary>The crop itself goes to a model that can see.</summary>
    public const string Vision = "vision";

    public static bool IsVision(string? id) =>
        string.Equals(id, Vision, StringComparison.OrdinalIgnoreCase);
}
