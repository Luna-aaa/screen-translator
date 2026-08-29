using System.IO;
using System.Text;
using ScreenTranslator.Config;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Translate;

/// <summary>
/// Headless check of the "look at the picture" route:
/// <code>ScreenTranslator.exe --testvision [baseUrl] [model] [key]</code>
///
/// The same idea as <see cref="TranslatorSelfTest"/>, with one difference that matters:
/// it sends a real picture of English text and prints what came back. A model that cannot
/// see will still answer the prompt, so only reading the reply tells you whether the
/// service is genuinely usable for this route.
///
/// Writes the outcome to %APPDATA%\ScreenTranslator\testvision.txt and exits 0 on success.
/// </summary>
internal static class VisionSelfTest
{
    /// <summary>What the probe image says, so the report can be checked at a glance.</summary>
    private const string ProbeText = "Good morning. The sky is blue.";

    public static async Task<int> RunAsync(string[] arguments)
    {
        var report = new StringBuilder();
        void Line(string text)
        {
            report.AppendLine(text);
            Log.Info($"[testvision] {text}");
        }

        Line("=== 看图直翻连通性检查 ===");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var config = ConfigStore.Load();
        var settings = config.Vision.Clone();

        if (arguments.Length > 0) settings.BaseUrl = arguments[0];
        if (arguments.Length > 1) settings.Model = arguments[1];
        if (arguments.Length > 2) settings.ApiKeyProtected = SecureStore.Protect(arguments[2]);

        Line($"当前路线：{(Pipelines.IsVision(config.Pipeline) ? "看图直翻" : "先识别文字再翻译（这次只是测试，不影响）")}");
        Line($"接口地址：{(string.IsNullOrWhiteSpace(settings.BaseUrl) ? "(空)" : settings.BaseUrl)}");
        Line($"模型：{(string.IsNullOrWhiteSpace(settings.Model) ? "(空)" : settings.Model)}");
        Line($"API Key：{(settings.HasKey ? "已配置" : "未配置")}");
        Line($"图片最大边长：{settings.MaxImageEdge}px");
        Line($"顺便取回原文：{(settings.IncludeOriginal ? "开" : "关")}");
        Line($"超时：{settings.TimeoutSeconds} 秒");
        Line("");

        var translator = new OpenAiCompatibleTranslator(settings, vision: true);

        if (!translator.IsConfigured)
        {
            Line("结果：未配置完整（地址 / Key / 模型 三者缺一）");
            Write(report);
            return 2;
        }

        var catalog = await translator.ListModelsAsync().ConfigureAwait(false);
        if (catalog.IsSuccess)
        {
            // Most entries a vendor lists cannot see; calling out the plausible ones saves
            // the reader from scanning fifty names for the handful that matter.
            var seeing = catalog.Models
                .Where(m => m.Contains("vl", StringComparison.OrdinalIgnoreCase)
                            || m.Contains("vision", StringComparison.OrdinalIgnoreCase)
                            || m.Contains("4v", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Line($"可用模型（{catalog.Models.Count}）：{string.Join(", ", catalog.Models)}");
            Line(seeing.Count > 0
                ? $"看起来能看图的（{seeing.Count}）：{string.Join(", ", seeing)}"
                : "名字里没有 vl / vision / 4v 的模型，这家可能不提供看图模型，或者命名方式不同。");
        }
        else
        {
            Line($"模型列表：拉取失败 - {catalog.Message}");
        }
        Line("");

        // Goes through TranslateAsync, not TestAsync, so the check exercises the real
        // capture path: the same prompt, the same image encoding, the same stream reader.
        var chunks = 0;
        var progress = new Progress<string>(_ => Interlocked.Increment(ref chunks));

        using var probeImage = ImageEncoder.BuildProbeImage();
        var probe = new TranslationRequest("", "", config.TargetLanguage, settings.ExtraPrompt)
        {
            Image = probeImage,
            WantOriginal = settings.IncludeOriginal,
        };

        var outcome = await translator.TranslateAsync(probe, progress).ConfigureAwait(false);

        UsageStore.Add(UsageStore.RouteVision, outcome.PromptTokens, outcome.CompletionTokens);
        Line($"状态：{outcome.Status}");
        if (outcome.IsSuccess)
        {
            Line($"用时：{outcome.ElapsedMs} ms");
            Line($"流式分段：{chunks}");
            if (outcome.Truncated) Line("注意：译文被截断了");
            Line("");
            var (translation, original) = settings.IncludeOriginal
                ? VisionReply.Split(outcome.Text)
                : (outcome.Text, "");

            Line($"图上写的是：{ProbeText}");
            Line($"模型读回来的译文：{translation}");
            if (settings.IncludeOriginal)
            {
                // Empty here means the model dropped the marker, and the popup will simply
                // have no 原文 to show — worth seeing in the report rather than guessing at.
                Line($"模型带回来的原文：{(original.Length == 0 ? "(没有，模型没按格式给)" : original)}");
            }
            Line("");
            Line("结果：请求通过。译文对不对得上图上的内容，看上面两行自己判断——");
            Line("      如果模型答非所问，多半是这个模型根本不会看图，换一个带 vl / vision 的。");
            Write(report);
            return 0;
        }

        Line($"给用户看的提示：{outcome.Message}");
        if (!string.IsNullOrWhiteSpace(outcome.Detail)) Line($"原始细节：{outcome.Detail}");
        Line("");
        Line("结果：失败");
        Write(report);
        return 1;
    }

    private static void Write(StringBuilder report)
    {
        try
        {
            Paths.EnsureDataDir();
            File.WriteAllText(Path.Combine(Paths.DataDir, "testvision.txt"), report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("写看图检查报告失败", ex);
        }
    }
}
