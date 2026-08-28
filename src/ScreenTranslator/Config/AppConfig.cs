using System.Text.Json.Serialization;

namespace ScreenTranslator.Config;

/// <summary>
/// Everything the user can change. Serialized to %APPDATA%\ScreenTranslator\config.json.
/// Secrets live only in *Protected fields (DPAPI ciphertext) — never in plaintext.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Bumped when the shape changes in a way that needs migration.
    /// v2: candidate OCR languages are seeded from what the machine can actually
    /// recognize, instead of a hard-coded English+Japanese pair.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ---- general ----------------------------------------------------------
    public string Hotkey { get; set; } = "Ctrl+Alt+Q";
    public bool AutoStart { get; set; }

    /// <summary>Keep a PNG of every crop.</summary>
    public bool SaveCaptures { get; set; } = true;

    /// <summary>Folder for saved crops. Empty means <see cref="Paths.DefaultCaptureDir"/>.</summary>
    public string CaptureDirectory { get; set; } = "";

    // ---- OCR --------------------------------------------------------------
    /// <summary>"auto", or a BCP-47 tag to pin the recognizer to one language.</summary>
    public string OcrSourceLanguage { get; set; } = "auto";

    /// <summary>When OcrSourceLanguage is "auto", only these engines are tried.</summary>
    public List<string> OcrCandidateLanguages { get; set; } = new() { "en-US", "ja-JP" };

    // ---- translation ------------------------------------------------------
    /// <summary>Id of the active <c>ITranslator</c> implementation.</summary>
    public string ActiveTranslator { get; set; } = OpenAiSettings.TranslatorId;

    public OpenAiSettings OpenAi { get; set; } = new();

    /// <summary>Target language for every engine. Fixed to Simplified Chinese for v1.</summary>
    public string TargetLanguage { get; set; } = "zh-Hans";

    public int RequestTimeoutSeconds { get; set; } = 30;

    public AppConfig Clone()
    {
        return new AppConfig
        {
            SchemaVersion = SchemaVersion,
            Hotkey = Hotkey,
            AutoStart = AutoStart,
            SaveCaptures = SaveCaptures,
            CaptureDirectory = CaptureDirectory,
            OcrSourceLanguage = OcrSourceLanguage,
            OcrCandidateLanguages = new List<string>(OcrCandidateLanguages),
            ActiveTranslator = ActiveTranslator,
            OpenAi = OpenAi.Clone(),
            TargetLanguage = TargetLanguage,
            RequestTimeoutSeconds = RequestTimeoutSeconds,
        };
    }
}

/// <summary>
/// Settings for the OpenAI-compatible /chat/completions adapter. One implementation
/// covers DeepSeek, DashScope, GLM, Kimi, OpenAI and most relay endpoints — switching
/// vendor is just a different base URL + model + key.
/// </summary>
public sealed class OpenAiSettings
{
    public const string TranslatorId = "openai-compatible";

    /// <summary>Id of the <see cref="ServicePresets"/> entry the user picked, or "custom".</summary>
    public string Preset { get; set; } = "deepseek";

    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>DPAPI ciphertext, base64. Read/write it through <see cref="SecureStore"/>.</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>Extra instruction appended to the translation prompt. Empty = engine default.</summary>
    public string ExtraPrompt { get; set; } = "";

    [JsonIgnore]
    public bool HasKey => !string.IsNullOrEmpty(ApiKeyProtected);

    public OpenAiSettings Clone() => new()
    {
        Preset = Preset,
        BaseUrl = BaseUrl,
        Model = Model,
        ApiKeyProtected = ApiKeyProtected,
        ExtraPrompt = ExtraPrompt,
    };
}

/// <summary>
/// Known OpenAI-compatible endpoints, so the user only has to paste a key.
/// Adding a vendor here is a one-line change — no code path depends on the list.
/// </summary>
public sealed record ServicePreset(string Id, string DisplayName, string BaseUrl, string Model, string SignupHint)
{
    public override string ToString() => DisplayName;
}

public static class ServicePresets
{
    public static readonly ServicePreset Custom =
        new("custom", "自定义（手动填写）", "", "", "任何兼容 OpenAI 接口格式的服务都可以填在这里。");

    public static readonly IReadOnlyList<ServicePreset> All = new[]
    {
        new ServicePreset("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat",
            "在 platform.deepseek.com 注册后，于「API keys」页面创建。"),
        new ServicePreset("dashscope", "通义千问（阿里云百炼）",
            "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus",
            "在 bailian.console.aliyun.com 开通后，于「API-KEY」页面创建。"),
        new ServicePreset("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash",
            "在 bigmodel.cn 注册后，于「API 密钥」页面创建。"),
        new ServicePreset("moonshot", "月之暗面 Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k",
            "在 platform.moonshot.cn 注册后，于「API Key 管理」页面创建。"),
        new ServicePreset("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini",
            "在 platform.openai.com 的「API keys」页面创建。国内访问可能需要自备网络条件。"),
        Custom,
    };

    public static ServicePreset Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Custom;
}
