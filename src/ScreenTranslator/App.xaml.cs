using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ScreenTranslator.Capture;
using ScreenTranslator.Config;
using ScreenTranslator.History;
using ScreenTranslator.Hotkey;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;
using ScreenTranslator.Translate;
using ScreenTranslator.Tray;
using ScreenTranslator.UI;
// UseWPF + UseWindowsForms means both frameworks are in the implicit usings, so the
// types that exist in each must be pinned explicitly.
using Application = System.Windows.Application;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;

namespace ScreenTranslator;

/// <summary>
/// Application root. There is no main window — the app lives in the tray and owns
/// three long-lived things: the single-instance guard, the tray icon, and the global
/// hotkey. Everything else is created on demand.
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private TrayIconHost? _tray;
    private GlobalHotkey? _hotkey;
    private SettingsWindow? _settings;
    private CaptureService? _capture;
    private ResultWindow? _result;
    private HistoryWindow? _history;
    private readonly IOcrProvider _ocr = new WindowsOcrProvider();

    /// <summary>
    /// Popups the user asked to keep. They outlive the capture that created them, which is
    /// the whole point, so they are tracked separately from the current one.
    /// </summary>
    private readonly List<ResultWindow> _pinnedWindows = new();

    private string? _hotkeyProblem;

    public static AppConfig Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的后台异常", args.ExceptionObject as Exception);

        // Diagnostic modes run before the single-instance check on purpose, so they work
        // while the app is already up in the tray.
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(AlignmentSelfTest.Run());
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--testkey", StringComparison.OrdinalIgnoreCase))
        {
            var rest = e.Args.Skip(1).ToArray();
            _ = TranslatorSelfTest.RunAsync(rest).ContinueWith(task =>
                Dispatcher.Invoke(() => Shutdown(task.IsFaulted ? 3 : task.Result)));
            return;
        }

        _guard = new SingleInstanceGuard();
        if (!_guard.TryAcquire())
        {
            // Another instance owns the tray. Hand it our intent and get out of the way.
            Log.Info("已有实例在运行，转交命令后退出");
            SingleInstanceGuard.SendToPrimary(IpcCommands.ShowSettings);
            Shutdown(0);
            return;
        }

        _guard.CommandReceived += OnIpcCommand;
        _guard.StartServer();

        Config = ConfigStore.Load(out var configIsNew);
        EnsureOcrCandidates(Config, configIsNew);
        AutoStart.Sync(Config.AutoStart);

        _capture = new CaptureService();

        _tray = new TrayIconHost();
        _tray.CaptureRequested += () => StartCapture(fromTray: true);
        _tray.SettingsRequested += ShowSettings;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.HistoryProvider = () => HistoryStore.All();
        _tray.HistoryEntryChosen += OnHistoryEntryChosen;
        _tray.HistoryWindowRequested += ShowHistory;
        _tray.ExitRequested += () => Shutdown(0);
        _tray.SetAutoStartChecked(Config.AutoStart);
        _tray.Show();

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += () => StartCapture(fromTray: false);
        RegisterStartupHotkey();

        LogOcrAvailability();

        // Recorded for the same reason as the OCR line above: "my history is gone" is
        // otherwise indistinguishable from "the switch is off" and from "the file failed
        // to parse", and this one line separates all three.
        Log.Info($"翻译历史：{HistoryStore.Count} 条，记录开关 {(Config.KeepHistory ? "开" : "关")}");

        Log.Info("启动完成");
    }

    /// <summary>
    /// Keeps the candidate language list in step with what the machine can actually
    /// recognize.
    ///
    /// An installed recognizer that is not in the candidate list is invisible to "auto",
    /// and the consequence is not a polite error - it is a confidently wrong answer.
    /// Chinese read by the Japanese engine comes back as plausible-looking Japanese kanji
    /// ("识别质量" turns into "沢別貭量"), because the Japanese engine knows those shapes
    /// and will happily map them to its own character set. So every installed language
    /// gets included by default; the user can uncheck any of them afterwards to go faster.
    /// </summary>
    private static void EnsureOcrCandidates(AppConfig config, bool isNewConfig)
    {
        var installed = LanguagePackHelper.InstalledTags()
            .Select(OcrLanguages.Find)
            .Where(l => l is not null)
            .Select(l => l!.Tag)
            .Distinct()
            .ToList();

        if (installed.Count == 0) return;   // nothing to seed from; keep what we have

        if (isNewConfig)
        {
            config.OcrCandidateLanguages = installed;
            config.SchemaVersion = AppConfig.CurrentSchemaVersion;
            ConfigStore.Save(config);
            Log.Info($"首次运行：候选识别语言设为系统已装的 {string.Join(", ", installed)}");
            return;
        }

        // One-time migration for configs written before this rule existed.
        if (config.SchemaVersion >= AppConfig.CurrentSchemaVersion) return;

        var added = installed
            .Where(tag => !config.OcrCandidateLanguages.Any(c => OcrLanguages.TagsMatch(c, tag)))
            .ToList();

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        if (added.Count > 0)
        {
            config.OcrCandidateLanguages = config.OcrCandidateLanguages.Concat(added).ToList();
            Log.Info($"配置升级：把系统已装但未启用的识别语言加入候选 - {string.Join(", ", added)}");
        }
        ConfigStore.Save(config);
    }

    /// <summary>
    /// Recorded at startup because "it just says no text found" is the symptom of a
    /// missing language pack, and this line is what turns that into a five-second diagnosis.
    /// </summary>
    private void LogOcrAvailability()
    {
        var installed = LanguagePackHelper.InstalledTags();
        Log.Info($"系统已装 OCR 识别语言（{installed.Count}）：{string.Join(", ", installed)}");

        var wanted = Config.OcrSourceLanguage == OcrRequest.Auto
            ? Config.OcrCandidateLanguages
            : new List<string> { Config.OcrSourceLanguage };

        var missing = LanguagePackHelper.Missing(wanted);
        if (missing.Count > 0)
            Log.Warn($"配置里要用但系统没装的语言：{string.Join(", ", missing.Select(m => $"{m.DisplayName}({m.Tag})"))}");
    }

    // ------------------------------------------------------------------ hotkey

    private void RegisterStartupHotkey()
    {
        var spec = HotkeySpec.TryParse(Config.Hotkey, out var parsed) && parsed.IsUsable
            ? parsed
            : HotkeySpec.Default;

        try
        {
            _hotkey!.Register(spec);
            _hotkeyProblem = null;
            _tray!.SetHotkeyHint(spec.ToString());
        }
        catch (HotkeyRegistrationException ex)
        {
            _hotkeyProblem = ex.Message;
            _tray!.SetHotkeyHint(null);
            _tray.Notify("快捷键没能注册", ex.Message, ToolTipIcon.Warning, 8000);
        }
    }

    // ----------------------------------------------------------------- actions

    private async void StartCapture(bool fromTray)
    {
        try
        {
            if (_capture is null) return;

            // Close the previous popup BEFORE freezing the screen. It is a topmost window,
            // so leaving it up bakes it into the screenshot and can leave it floating above
            // the dimming mask - which reads as "the hotkey did nothing".
            CloseResultWindow();

            if (fromTray)
            {
                // Let the tray menu finish closing and the desktop repaint, otherwise the
                // menu itself ends up frozen into the screenshot.
                await Task.Delay(150);
            }

            // Pinned popups are topmost, so leaving them up would photograph them into the
            // frozen desktop and float them above the dimming mask.
            CaptureOutcome outcome;
            SetPinnedVisible(false);
            try
            {
                outcome = await _capture.CaptureAsync(
                    Config.SaveCaptures, Paths.ResolveCaptureDir(Config.CaptureDirectory));
            }
            finally
            {
                SetPinnedVisible(true);
            }

            using (outcome)
            {
                switch (outcome.Status)
                {
                    case CaptureStatus.Success:
                        OnCaptureSucceeded(outcome);
                        break;
                    case CaptureStatus.Failed:
                        _tray?.Notify("截图失败", outcome.Error ?? "未知错误，详情见日志。", ToolTipIcon.Error, 8000);
                        break;
                    // Cancelled and Busy are deliberately silent - the user knows what they did.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("框选流程出错", ex);
            _tray?.Notify("出错了", $"{ex.Message}（详情见日志）", ToolTipIcon.Error, 8000);
        }
    }

    private void OnCaptureSucceeded(CaptureOutcome outcome)
    {
        var image = outcome.TakeImage();
        if (image is null)
        {
            Log.Warn("截图成功但没有拿到位图");
            return;
        }

        CloseResultWindow();

        var window = new ResultWindow(image, outcome.ScreenRect, PopupThemes.Find(Config.PopupTheme));
        window.RetryHandler = () => RunRecognitionAsync(window);
        window.ObstaclesProvider = () => PinnedRectsExcept(window);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_result, window)) _result = null;
            _pinnedWindows.Remove(window);
        };

        _result = window;
        window.SetRecognizing();
        window.ShowNoActivate();

        _ = RunRecognitionAsync(window);
    }

    /// <summary>
    /// A new capture supersedes the current popup — unless the user pinned it, which is
    /// exactly the request to keep it around while they go look up the next thing.
    /// </summary>
    private void CloseResultWindow()
    {
        var window = _result;
        _result = null;
        if (window is null) return;

        if (window.IsPinned)
        {
            if (!_pinnedWindows.Contains(window)) _pinnedWindows.Add(window);
            Log.Info($"小窗已钉住，保留（当前 {_pinnedWindows.Count} 个）");
            return;
        }

        window.Close();
    }

    /// <summary>
    /// Where the pinned popups currently sit, so a new one can avoid covering them.
    /// Excludes the asking window, which is allowed to sit exactly where it already is.
    /// </summary>
    private IReadOnlyList<System.Drawing.Rectangle> PinnedRectsExcept(ResultWindow asking) =>
        _pinnedWindows.Where(w => !ReferenceEquals(w, asking))
                      .Select(w => w.ScreenRect)
                      .ToList();

    /// <summary>Iterates a copy: a popup can close itself while this runs.</summary>
    private void SetPinnedVisible(bool visible)
    {
        foreach (var window in _pinnedWindows.ToList())
        {
            if (visible) window.ShowAfterCapture();
            else window.HideForCapture();
        }
    }

    /// <summary>
    /// Phase 3 will feed the recognized text into a translator; for now the popup shows
    /// the OCR output itself, which is what makes recognition quality judgeable.
    /// </summary>
    private async Task RunRecognitionAsync(ResultWindow window)
    {
        try
        {
            window.SetRecognizing();

            var request = new OcrRequest(Config.OcrSourceLanguage, Config.OcrCandidateLanguages);
            var ocr = await _ocr.RecognizeAsync(window.Image, request, window.Lifetime);

            if (window.Lifetime.IsCancellationRequested) return;   // closed while we worked

            if (ocr.Status != OcrStatus.Success)
            {
                window.SetOcrResult(ocr);
                return;
            }

            await RunTranslationAsync(window, ocr);
        }
        catch (OperationCanceledException)
        {
            // The popup was closed; nothing to report.
        }
        catch (Exception ex)
        {
            Log.Error("识别过程出错", ex);
            if (!window.Lifetime.IsCancellationRequested) window.SetOcrResult(OcrOutcome.Failed(ex.Message));
        }
    }

    private async Task RunTranslationAsync(ResultWindow window, OcrOutcome ocr)
    {
        window.SetTranslating(ocr);

        // Re-translating reuses the recognized text, so a bad translation costs one request
        // instead of a whole re-read of the image.
        window.RetranslateHandler = () => RunTranslationAsync(window, ocr);

        var translator = CreateTranslator();
        if (!translator.IsConfigured)
        {
            window.SetTranslationResult(TranslationOutcome.NotConfigured());
            return;
        }

        var request = new TranslationRequest(
            ocr.Text, ocr.LanguageTag, Config.TargetLanguage, Config.OpenAi.ExtraPrompt);

        // Progress captures this (UI) thread's synchronization context, so the translator
        // can report from wherever it likes and the popup still updates safely.
        IProgress<string>? progress = Config.StreamTranslation
            ? new Progress<string>(window.ShowPartialTranslation)
            : null;

        var result = await translator.TranslateAsync(request, progress, window.Lifetime);

        if (window.Lifetime.IsCancellationRequested) return;
        window.SetTranslationResult(result);

        RememberTranslation(ocr, result);
    }

    private static void RememberTranslation(OcrOutcome ocr, TranslationOutcome result)
    {
        if (!Config.KeepHistory) return;
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text)) return;

        HistoryStore.Add(new TranslationRecord
        {
            SourceLanguage = OcrLanguages.DisplayFor(ocr.LanguageTag),
            Original = ocr.Text,
            Translation = result.Text,
        });
    }

    // ----------------------------------------------------------------- history

    /// <summary>
    /// Picking an entry from the tray copies it. Re-showing it beside the original screen
    /// region would be wrong - by now there is something else there.
    /// </summary>
    private void OnHistoryEntryChosen(TranslationRecord record)
    {
        var text = string.IsNullOrWhiteSpace(record.Translation) ? record.Original : record.Translation;
        try
        {
            System.Windows.Clipboard.SetText(text);
            _tray?.Notify("已复制", record.Summary(60));
        }
        catch (Exception ex)
        {
            Log.Warn($"从托盘复制历史失败：{ex.Message}");
            _tray?.Notify("复制失败", "剪贴板被别的程序占着，过一秒再试。", ToolTipIcon.Warning);
        }
    }

    private void ShowHistory()
    {
        if (_history is not null)
        {
            if (_history.WindowState == WindowState.Minimized) _history.WindowState = WindowState.Normal;
            _history.Activate();
            return;
        }

        _history = new HistoryWindow();
        _history.Closed += (_, _) => _history = null;
        _history.Show();
        _history.Activate();
    }

    /// <summary>
    /// Built fresh each time so edits in the settings window take effect on the very next
    /// capture, with no restart and no stale key cached anywhere.
    /// </summary>
    public static ITranslator CreateTranslator() =>
        new OpenAiCompatibleTranslator(Config.OpenAi, Config.RequestTimeoutSeconds);

    private void OnAutoStartToggled(bool enabled)
    {
        if (!AutoStart.SetEnabled(enabled))
        {
            _tray?.Notify("设置失败", "写入开机自启设置失败，详情见日志。", ToolTipIcon.Error);
            _tray?.SetAutoStartChecked(AutoStart.IsEnabled());
            return;
        }

        Config.AutoStart = enabled;
        ConfigStore.Save(Config);

        // Mirror the change into the open settings window without touching anything
        // else the user may be part-way through editing.
        _settings?.SetAutoStartChecked(enabled);
    }

    private void OnIpcCommand(string command)
    {
        // Arrives on a pipe thread.
        Dispatcher.Invoke(() =>
        {
            switch (command)
            {
                case IpcCommands.ShowSettings:
                    ShowSettings();
                    break;
                case IpcCommands.Capture:
                    StartCapture(fromTray: false);
                    break;
                default:
                    Log.Warn($"未知的实例间命令：{command}");
                    break;
            }
        });
    }

    // ---------------------------------------------------------------- settings

    private void ShowSettings()
    {
        if (_settings is not null)
        {
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
            _settings.Topmost = true;
            _settings.Topmost = false;
            return;
        }

        _settings = new SettingsWindow(Config) { SaveHandler = ApplySettings };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();

        PushHotkeyStatus();
    }

    private void PushHotkeyStatus()
    {
        if (_settings is null) return;
        if (_hotkeyProblem is not null)
        {
            _settings.SetHotkeyStatus(_hotkeyProblem, ok: false);
        }
        else
        {
            _settings.SetHotkeyStatus($"快捷键 {_hotkey?.Current} 已生效。", ok: true);
        }
    }

    /// <summary>
    /// Applies edited settings for real. Returns null on success, or a message to show
    /// the user. Order matters: the hotkey is the only step that can fail in a way the
    /// user must fix, so it goes first and rolls back on failure.
    /// </summary>
    private string? ApplySettings(AppConfig updated)
    {
        if (!HotkeySpec.TryParse(updated.Hotkey, out var spec) || !spec.IsUsable)
            return "快捷键无效，请重新设置。";

        var previous = _hotkey?.Current;
        try
        {
            _hotkey!.Register(spec);
            _hotkeyProblem = null;
        }
        catch (HotkeyRegistrationException ex)
        {
            RestoreHotkey(previous);
            _hotkeyProblem = ex.Message;
            return ex.Message;
        }

        // Any later failure has to undo the hotkey too, otherwise the running app answers to
        // a hotkey that neither the config file nor the tray menu agrees with, and the
        // change evaporates on the next restart.
        if (!AutoStart.SetEnabled(updated.AutoStart))
        {
            RestoreHotkey(previous);
            return "开机自启设置写入失败，详情见日志。";
        }

        if (!ConfigStore.Save(updated))
        {
            RestoreHotkey(previous);
            AutoStart.SetEnabled(Config.AutoStart);
            return "配置文件保存失败，详情见日志。";
        }

        Config = updated;
        _tray?.SetHotkeyHint(spec.ToString());
        _tray?.SetAutoStartChecked(updated.AutoStart);
        PushHotkeyStatus();
        return null;
    }

    /// <summary>Puts the previously working hotkey back after a failed settings apply.</summary>
    private void RestoreHotkey(HotkeySpec? previous)
    {
        if (previous is null) return;
        try
        {
            _hotkey!.Register(previous);
            _tray?.SetHotkeyHint(previous.ToString());
        }
        catch (Exception ex)
        {
            // Someone else grabbed it in the meantime; nothing left to fall back to.
            Log.Warn($"回退到原快捷键 {previous} 失败：{ex.Message}");
            _tray?.SetHotkeyHint(null);
        }
    }

    // ---------------------------------------------------------------- shutdown

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("界面线程未处理异常", e.Exception);
        e.Handled = true;
        _tray?.Notify("出错了", $"{e.Exception.Message}（详情见日志）", ToolTipIcon.Error, 8000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"退出，代码 {e.ApplicationExitCode}");
        _hotkey?.Dispose();
        _tray?.Dispose();
        _guard?.Dispose();
        base.OnExit(e);
    }
}
