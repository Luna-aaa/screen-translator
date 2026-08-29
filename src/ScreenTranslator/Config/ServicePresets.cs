namespace ScreenTranslator.Config;

/// <summary>
/// A known endpoint, so the user only has to paste a key.
/// Adding a vendor is a one-line change — no code path depends on the list.
/// </summary>
public sealed record ServicePreset(string Id, string DisplayName, string BaseUrl, string Model, string SignupHint)
{
    public override string ToString() => DisplayName;
}

/// <summary>Text-only services, for 识文翻译.</summary>
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

/// <summary>
/// Services whose endpoint accepts an image in the message, for 看图翻译 and 全屏翻译.
/// Separate from <see cref="ServicePresets"/> because most entries there cannot see
/// pictures at all, and offering them here would only produce a confusing 400.
/// </summary>
public static class VisionPresets
{
    public static readonly ServicePreset Custom =
        new("custom", "自定义（手动填写）", "", "",
            "任何兼容 OpenAI 接口、并且支持在消息里带图片的服务都可以填在这里。");

    public static readonly IReadOnlyList<ServicePreset> All = new[]
    {
        new ServicePreset("qwen-vl", "通义千问 VL（阿里云百炼）",
            "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-vl-max-latest",
            "在 bailian.console.aliyun.com 开通后，于「API-KEY」页面创建。"
            + "想省钱可以把模型换成 qwen-vl-plus。"),

        new ServicePreset("glm-4v", "智谱 GLM-4V", "https://open.bigmodel.cn/api/paas/v4", "glm-4v-flash",
            "在 bigmodel.cn 注册后，于「API 密钥」页面创建。glm-4v-flash 目前免费。"),

        new ServicePreset("kimi-vision", "月之暗面 Kimi", "https://api.moonshot.cn/v1",
            "moonshot-v1-8k-vision-preview",
            "在 platform.moonshot.cn 注册后，于「API Key 管理」页面创建。"),

        new ServicePreset("openai-vision", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini",
            "在 platform.openai.com 的「API keys」页面创建。国内访问可能需要自备网络条件。"),

        Custom,
    };

    public static ServicePreset Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Custom;
}
