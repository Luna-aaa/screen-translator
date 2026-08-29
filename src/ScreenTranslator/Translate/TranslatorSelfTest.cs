using System.IO;
using System.Text;
using ScreenTranslator.Config;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Translate;

/// <summary>
/// Headless check of the translation setup:
/// <code>ScreenTranslator.exe --testkey [baseUrl] [model] [key]</code>
///
/// With no arguments it tests the saved configuration — a quick way to confirm a key
/// works without opening the settings window. The overrides exist so the error messages
/// can be exercised against endpoints that fail on purpose; pass only throwaway values
/// there, since anything on a command line is visible to other processes.
///
/// Writes the outcome to %APPDATA%\ScreenTranslator\testkey.txt and exits 0 on success.
/// </summary>
internal static class TranslatorSelfTest
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var report = new StringBuilder();
        void Line(string text)
        {
            report.AppendLine(text);
            Log.Info($"[testkey] {text}");
        }

        Line("=== 翻译服务连通性检查 ===");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var config = ConfigStore.Load();
        var settings = config.OpenAi.Clone();

        if (arguments.Length > 0) settings.BaseUrl = arguments[0];
        if (arguments.Length > 1) settings.Model = arguments[1];
        if (arguments.Length > 2) settings.ApiKeyProtected = SecureStore.Protect(arguments[2]);

        Line($"接口地址：{(string.IsNullOrWhiteSpace(settings.BaseUrl) ? "(空)" : settings.BaseUrl)}");
        Line($"模型：{(string.IsNullOrWhiteSpace(settings.Model) ? "(空)" : settings.Model)}");
        Line($"API Key：{(settings.HasKey ? "已配置" : "未配置")}");
        Line($"超时：{settings.TimeoutSeconds} 秒");
        Line("");

        var translator = new OpenAiCompatibleTranslator(settings, vision: false);

        if (!translator.IsConfigured)
        {
            Line("结果：未配置完整（地址 / Key / 模型 三者缺一）");
            Write(report);
            return 2;
        }

        var catalog = await translator.ListModelsAsync().ConfigureAwait(false);
        if (catalog.IsSuccess)
        {
            Line($"可用模型（{catalog.Models.Count}）：{string.Join(", ", catalog.Models)}");
        }
        else
        {
            Line($"模型列表：拉取失败 - {catalog.Message}");
        }
        Line("");

        // Goes through TranslateAsync rather than TestAsync so the check exercises the
        // real path end to end - prompt building included - and can report whether the
        // reply actually arrived in pieces.
        var chunks = 0;
        var progress = new Progress<string>(_ => Interlocked.Increment(ref chunks));
        var probe = new TranslationRequest("hello", "en-US", config.TargetLanguage, config.OpenAi.ExtraPrompt);

        var outcome = await translator.TranslateAsync(probe, progress).ConfigureAwait(false);

        Line($"状态：{outcome.Status}");
        if (outcome.IsSuccess)
        {
            Line($"用时：{outcome.ElapsedMs} ms");
            // 1 means the service answered in one piece; more means it really streamed.
            Line($"流式分段：{chunks}");
            if (outcome.Truncated) Line("注意：译文被截断了");
            Line($"返回：{outcome.Text}");
            Line("");
            Line("结果：通过");
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
            File.WriteAllText(Path.Combine(Paths.DataDir, "testkey.txt"), report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("写翻译检查报告失败", ex);
        }
    }
}
