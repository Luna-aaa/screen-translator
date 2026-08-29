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
                // Report this as "created": the caller seeds a fresh config from the machine
                // and writes it back, which is exactly what a recovered-from-nothing config
                // needs. Reporting false would leave it unseeded and unsaved, repeating
                // every launch.
                Log.Warn("配置文件内容为空，改用默认配置");
                created = true;
                return new AppConfig();
            }

            var migrated = MigrateToPerRoute(cfg);
            Normalize(cfg);
            if (migrated) Save(cfg);

            Log.Info($"已加载配置：框选={cfg.Hotkey}／整屏={(cfg.OverlayHotkey.Length == 0 ? "关" : cfg.OverlayHotkey)}"
                     + $"，框选走 {cfg.Pipeline}，识文={cfg.OpenAi.Model}，看图={cfg.Vision.Model}，全屏={cfg.Snapshot.Model}");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error("读取配置失败，改用默认配置", ex);
            BackupCorruptFile();
            created = true;   // see the null case above
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
            LogChanges(config);

            lock (Gate)
            {
                // Write to a temp file first so a crash mid-write cannot leave a
                // half-written config behind.
                var tmp = Paths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, json);
                KeepPreviousVersion(json);
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

    /// <summary>
    /// Moves a pre-v3 config's shared settings into each route's own copy.
    ///
    /// Runs once, and errs towards keeping what the user chose: the old single timeout and
    /// theme were arrived at deliberately, so every route starts from them rather than
    /// from a fresh default. The whole-screen route is the exception — it did not exist
    /// when those values were picked, and a 30-second budget sized for one line of subtitle
    /// would cut off a request covering forty blocks.
    /// </summary>
    private static bool MigrateToPerRoute(AppConfig cfg)
    {
        if (cfg.SchemaVersion >= AppConfig.CurrentSchemaVersion) return false;

        foreach (var route in new PopupRouteSettings[] { cfg.OpenAi, cfg.Vision })
        {
            route.TimeoutSeconds = cfg.RequestTimeoutSeconds;
            route.StreamTranslation = cfg.StreamTranslation;
            route.PopupTheme = cfg.PopupTheme;
            route.KeepHistory = cfg.KeepHistory;
            route.SaveCaptures = cfg.SaveCaptures;
            route.CaptureDirectory = cfg.CaptureDirectory;
        }

        // Seeded from the picture route, which is the one it behaves most like — same
        // vendor, same key, same kind of request.
        if (cfg.Vision.HasKey)
        {
            cfg.Snapshot.Preset = cfg.Vision.Preset;
            cfg.Snapshot.BaseUrl = cfg.Vision.BaseUrl;
            cfg.Snapshot.Model = cfg.Vision.Model;
            cfg.Snapshot.ApiKeyProtected = cfg.Vision.ApiKeyProtected;
            Log.Info("配置升级：全屏翻译的服务先沿用「看图翻译」那一套，可以在设置里单独改");
        }

        cfg.SchemaVersion = AppConfig.CurrentSchemaVersion;
        Log.Info("配置升级到 v3：超时／小窗／截图／历史 改为每条路各自一份");
        return true;
    }

    private static void Normalize(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Hotkey)) cfg.Hotkey = "Ctrl+Alt+Q";
        // Blank here is meaningful (the snapshot hotkey is switched off), so unlike the
        // capture hotkey it is normalised to empty rather than back to a default.
        cfg.OverlayHotkey = (cfg.OverlayHotkey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cfg.TargetLanguage)) cfg.TargetLanguage = "zh-Hans";
        if (string.IsNullOrWhiteSpace(cfg.ActiveTranslator)) cfg.ActiveTranslator = OpenAiSettings.TranslatorId;
        if (string.IsNullOrWhiteSpace(cfg.OcrSourceLanguage)) cfg.OcrSourceLanguage = "auto";

        // An unrecognised value falls back to the route that always works rather than
        // leaving the app pointed at a pipeline that does not exist.
        if (!Pipelines.IsVision(cfg.Pipeline)) cfg.Pipeline = Pipelines.Classic;

        // A hand-edited "null" for any of these would otherwise take the whole config down,
        // and the user would see "配置文件损坏" for a field they were only experimenting with.
        cfg.OpenAi ??= new OpenAiSettings();
        cfg.Vision ??= new VisionSettings();
        cfg.Snapshot ??= new SnapshotSettings();

        NormalizeRoute(cfg.OpenAi);
        NormalizeRoute(cfg.Vision);
        NormalizeRoute(cfg.Snapshot);

        if (string.IsNullOrWhiteSpace(cfg.OpenAi.PopupTheme)) cfg.OpenAi.PopupTheme = "dark";
        if (string.IsNullOrWhiteSpace(cfg.Vision.PopupTheme)) cfg.Vision.PopupTheme = "dark";

        cfg.OcrCandidateLanguages = cfg.OcrCandidateLanguages
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cfg.OcrCandidateLanguages.Count == 0)
            cfg.OcrCandidateLanguages = new List<string> { "en-US", "ja-JP" };

    }

    private static void NormalizeRoute(RouteSettings route)
    {
        route.BaseUrl = (route.BaseUrl ?? "").Trim().TrimEnd('/');
        route.Model = (route.Model ?? "").Trim();
        route.ExtraPrompt = (route.ExtraPrompt ?? "").Trim();
        route.CaptureDirectory = (route.CaptureDirectory ?? "").Trim();
        route.TimeoutSeconds = Math.Clamp(route.TimeoutSeconds, 5, 300);
        route.MaxImageEdge = Math.Clamp(route.MaxImageEdge, 640, 3200);
    }

    /// <summary>
    /// Names every setting this save is about to change.
    ///
    /// Exists because a value once reset itself to its default and no amount of reading the
    /// code found the path that did it. Guessing at a cause costs more than one log line
    /// per save: the next time something rewrites a field nobody touched, the line says
    /// which field, what it was, and what it became.
    /// </summary>
    private static void LogChanges(AppConfig incoming)
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return;

            var previous = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile), Options);
            if (previous is null) return;

            var changes = new List<string>();
            Compare("框选快捷键", previous.Hotkey, incoming.Hotkey);
            Compare("整屏快捷键", previous.OverlayHotkey, incoming.OverlayHotkey);
            Compare("翻译方式", previous.Pipeline, incoming.Pipeline);
            CompareRoute("识文", previous.OpenAi, incoming.OpenAi);
            CompareRoute("看图", previous.Vision, incoming.Vision);
            CompareRoute("全屏", previous.Snapshot, incoming.Snapshot);

            if (changes.Count > 0) Log.Info($"配置变更：{string.Join("；", changes)}");

            void Compare(string name, object? before, object? after)
            {
                var a = before?.ToString() ?? "";
                var b = after?.ToString() ?? "";
                if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add($"{name} {a} → {b}");
            }

            void CompareRoute(string label, RouteSettings? before, RouteSettings? after)
            {
                if (before is null || after is null) return;
                Compare($"{label}.服务", before.Preset, after.Preset);
                Compare($"{label}.地址", before.BaseUrl, after.BaseUrl);
                Compare($"{label}.模型", before.Model, after.Model);
                Compare($"{label}.超时", before.TimeoutSeconds, after.TimeoutSeconds);
                Compare($"{label}.图片边长", before.MaxImageEdge, after.MaxImageEdge);
                Compare($"{label}.截图目录", before.CaptureDirectory, after.CaptureDirectory);
                // The key is never printed; only whether one is present.
                Compare($"{label}.有无Key", before.HasKey, after.HasKey);

                if (before is PopupRouteSettings p1 && after is PopupRouteSettings p2)
                {
                    Compare($"{label}.流式", p1.StreamTranslation, p2.StreamTranslation);
                    Compare($"{label}.配色", p1.PopupTheme, p2.PopupTheme);
                    Compare($"{label}.历史", p1.KeepHistory, p2.KeepHistory);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"记录配置变更失败（不影响保存）：{ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the last different version of the file as config.json.bak.
    ///
    /// Settings have gone missing twice — once to a test script that was interrupted
    /// half-way, once to a cause that was never pinned down — and in both cases the only
    /// thing that made recovery possible was having a copy lying around. An API key or a
    /// hand-typed model name is minutes of the user's work; a spare copy of a 3 KB file
    /// costs nothing.
    ///
    /// Skipped when the content is unchanged, so a run of no-op saves cannot push the last
    /// good version out of the backup.
    /// </summary>
    private static void KeepPreviousVersion(string newJson)
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return;

            var current = File.ReadAllText(Paths.ConfigFile);
            if (string.Equals(current, newJson, StringComparison.Ordinal)) return;

            File.Copy(Paths.ConfigFile, Paths.ConfigFile + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            // A failed backup must never stop the save it was protecting.
            Log.Warn($"备份上一版配置失败（不影响保存）：{ex.Message}");
        }
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
