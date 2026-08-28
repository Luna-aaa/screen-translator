using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Config;

/// <summary>Loads/saves <see cref="AppConfig"/>. Never throws at the call site.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // The user is expected to open this file (to confirm the API key really is
        // encrypted), so escape as little as possible: the stricter encoders turn both
        // Chinese text and the "+" in a hotkey into \uXXXX noise. "Unsafe" here only
        // means "not pre-escaped for HTML", which is irrelevant - this never goes in a page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object Gate = new();

    public static AppConfig Load() => Load(out _);

    /// <param name="created">True when no config existed and defaults were written.</param>
    public static AppConfig Load(out bool created)
    {
        created = false;
        try
        {
            Paths.EnsureDataDir();
            if (!File.Exists(Paths.ConfigFile))
            {
                Log.Info("未找到配置文件，使用默认配置");
                created = true;
                var fresh = new AppConfig();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(Paths.ConfigFile);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg is null)
            {
                Log.Warn("配置文件内容为空，改用默认配置");
                return new AppConfig();
            }

            Normalize(cfg);
            Log.Info($"已加载配置：hotkey={cfg.Hotkey}, translator={cfg.ActiveTranslator}, preset={cfg.OpenAi.Preset}");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error("读取配置失败，改用默认配置", ex);
            BackupCorruptFile();
            return new AppConfig();
        }
    }

    public static bool Save(AppConfig config)
    {
        try
        {
            Paths.EnsureDataDir();
            Normalize(config);
            var json = JsonSerializer.Serialize(config, Options);

            lock (Gate)
            {
                // Write to a temp file first so a crash mid-write cannot leave a
                // half-written config behind.
                var tmp = Paths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, Paths.ConfigFile, overwrite: true);
            }

            Log.Info("配置已保存");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败", ex);
            return false;
        }
    }

    private static void Normalize(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Hotkey)) cfg.Hotkey = "Ctrl+Alt+Q";
        if (string.IsNullOrWhiteSpace(cfg.TargetLanguage)) cfg.TargetLanguage = "zh-Hans";
        if (string.IsNullOrWhiteSpace(cfg.ActiveTranslator)) cfg.ActiveTranslator = OpenAiSettings.TranslatorId;
        if (string.IsNullOrWhiteSpace(cfg.OcrSourceLanguage)) cfg.OcrSourceLanguage = "auto";

        cfg.RequestTimeoutSeconds = Math.Clamp(cfg.RequestTimeoutSeconds, 5, 300);
        cfg.CaptureDirectory = (cfg.CaptureDirectory ?? "").Trim();

        cfg.OcrCandidateLanguages = cfg.OcrCandidateLanguages
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cfg.OcrCandidateLanguages.Count == 0)
            cfg.OcrCandidateLanguages = new List<string> { "en-US", "ja-JP" };

        cfg.OpenAi.BaseUrl = (cfg.OpenAi.BaseUrl ?? "").Trim().TrimEnd('/');
        cfg.OpenAi.Model = (cfg.OpenAi.Model ?? "").Trim();
    }

    private static void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return;
            var backup = Paths.ConfigFile + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(Paths.ConfigFile, backup, overwrite: true);
            Log.Warn($"损坏的配置文件已备份到 {backup}");
        }
        catch
        {
            // ignored
        }
    }
}
