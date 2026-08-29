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
    private GlobalHotkey? _overlayHotkey;
    private SettingsWindow? _settings;
    private CaptureService? _capture;
    private ResultWindow? _result;
    private HistoryWindow? _history;
    private readonly IOcrProvider _ocr = new WindowsOcrProvider();

    /// <summary>
    /// Phase 6's whole-screen snapshot. Given its own recognizer instance rather than a
    /// cast of <see cref="_ocr"/>: the provider is stateless, and this keeps the popup
    /// path free of any dependency on the layout interface.
    /// </summary>
    private readonly Overlay.SnapshotService _snapshot = new(new WindowsOcrProvider());

    /// <summary>
    /// Popups the user asked to keep. They outlive the capture that created them, which is
    /// the whole point, so they are tracked separately from the current one.
    /// </summary>
    private readonly List<ResultWindow> _pinnedWindows = new();

    private string? _hotkeyProblem;
    private string? _overlayHotkeyProblem;

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

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--testvision", StringComparison.OrdinalIgnoreCase))
        {
            var rest = e.Args.Skip(1).ToArray();
            _ = VisionSelfTest.RunAsync(rest).ContinueWith(task =>
                Dispatcher.Invoke(() => Shutdown(task.IsFaulted ? 3 : task.Result)));
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--testclipboard", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(ClipboardSelfTest.Run(e.Args.Skip(1).ToArray()));
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--testsnapshot", StringComparison.OrdinalIgnoreCase))
        {
            var rest = e.Args.Skip(1).ToArray();
            _ = Overlay.SnapshotSelfTest.RunAsync(rest).ContinueWith(task =>
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
        Paths.MigrateLegacyCropFolder();
        EnsureOcrCandidates(Config, configIsNew);
        AutoStart.Sync(Config.AutoStart);

        _capture = new CaptureService();

        _tray = new TrayIconHost();
        _tray.CaptureRequested += () => StartCapture(fromTray: true);
        _tray.SnapshotRequested += () => StartSnapshot(fromTray: true);
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

        // A second, independent registration rather than one hotkey with two meanings:
        // each has its own hidden sink window, so one being refused (already taken by
        // another program) leaves the other working.
        _overlayHotkey = new GlobalHotkey();
        _overlayHotkey.Pressed += () => StartSnapshot(fromTray: false);

        RegisterStartupHotkey();
        RegisterOverlayHotkey();

        LogOcrAvailability();

        // Recorded for the same reason as the OCR line above: "my history is gone" is
        // otherwise indistinguishable from "the switch is off" and from "the file failed
        // to parse", and this one line separates all three.
        Log.Info($"翻译历史：{HistoryStore.Count} 条，记录开关 识文={(Config.OpenAi.KeepHistory ? "开" : "关")}"
                 + $"／看图={(Config.Vision.KeepHistory ? "开" : "关")}");

        Log.Info(Pipelines.IsVision(Config.Pipeline)
            ? $"框选翻译走：看图翻译（{Config.Vision.Model}）"
            : $"框选翻译走：识文翻译（{Config.OpenAi.Model}）");
        Log.Info($"全屏翻译用：{Config.Snapshot.Model}");

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

    /// <summary>
    /// Registers the snapshot hotkey, or leaves it unregistered when the field is blank.
    /// Unlike the capture hotkey, failing here is not worth a balloon: the tray menu item
    /// still runs the feature, so nothing is actually unreachable.
    /// </summary>
    private void RegisterOverlayHotkey()
    {
        _overlayHotkey?.Unregister();
        _overlayHotkeyProblem = null;
        _tray?.SetSnapshotHotkeyHint(null);

        if (string.IsNullOrWhiteSpace(Config.OverlayHotkey)) return;
        if (!HotkeySpec.TryParse(Config.OverlayHotkey, out var spec) || !spec.IsUsable)
        {
            _overlayHotkeyProblem = $"整屏翻译快捷键「{Config.OverlayHotkey}」无效，已忽略。";
            Log.Warn(_overlayHotkeyProblem);
            return;
        }

        try
        {
            _overlayHotkey!.Register(spec);
            _tray?.SetSnapshotHotkeyHint(spec.ToString());
        }
        catch (HotkeyRegistrationException ex)
        {
            _overlayHotkeyProblem = ex.Message;
            Log.Warn($"整屏翻译快捷键注册失败：{ex.Message}");
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
                // Which route is active decides where its crop is filed and whether it is
                // filed at all — the two routes keep separate folders.
                var route = Config.ActiveRoute;
                outcome = await _capture.CaptureAsync(
                    route.SaveCaptures, Paths.ResolveCropDir(route.CaptureDirectory));
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

    /// <summary>
    /// Phase 6: freeze the screen, translate everything on it, and draw the results back
    /// over the words they came from.
    ///
    /// Deliberately a separate entry point from <see cref="StartCapture"/>. The framing
    /// flow the user already verified is untouched — this neither shares its hotkey nor
    /// its popup, and switching it on changes nothing about how the old one behaves.
    /// </summary>
    private async void StartSnapshot(bool fromTray)
    {
        try
        {
            if (_snapshot.IsBusy) return;

            // Same reason as the framing flow: these are topmost windows, so leaving them
            // up photographs them into the frozen desktop and floats them over the result.
            CloseResultWindow();
            if (fromTray) await Task.Delay(150);

            SetPinnedVisible(false);
            try
            {
                await _snapshot.RunAsync(Config);
            }
            finally
            {
                SetPinnedVisible(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("整屏翻译流程出错", ex);
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

        var window = new ResultWindow(
            image, outcome.ScreenRect, PopupThemes.Find(Config.ActiveRoute.PopupTheme));
        window.SetTargetLabel(TargetLanguages.Find(Config.TargetLanguage).ShortName);
        window.RetryHandler = () => RunPipelineAsync(window);
        window.ObstaclesProvider = () => PinnedRectsExcept(window);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_result, window)) _result = null;
            _pinnedWindows.Remove(window);
        };

        _result = window;
        window.SetRecognizing();
        window.ShowNoActivate();

        _ = RunPipelineAsync(window);
    }

    /// <summary>
    /// Picks the route for this capture. Read from the config every time rather than
    /// cached, so switching in the settings window takes effect on the very next capture
    /// with no restart.
    /// </summary>
    private Task RunPipelineAsync(ResultWindow window) =>
        Pipelines.IsVision(Config.Pipeline) ? RunVisionAsync(window) : RunRecognitionAsync(window);

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
        IProgress<string>? progress = Config.OpenAi.StreamTranslation
            ? new Progress<string>(window.ShowPartialTranslation)
            : null;

        var result = await translator.TranslateAsync(request, progress, window.Lifetime);

        if (window.Lifetime.IsCancellationRequested) return;
        window.SetTranslationResult(result);

        UsageStore.Add(UsageStore.RouteClassic, result.PromptTokens, result.CompletionTokens);
        RememberTranslation(ocr, result);
    }

    /// <summary>
    /// The picture goes straight to a model that can read it — no OCR, no language
    /// guessing, and mixed scripts in one image all get translated instead of only
    /// whichever one the scorer picked.
    /// </summary>
    private async Task RunVisionAsync(ResultWindow window)
    {
        try
        {
            window.SetVisionTranslating();

            // Re-translating re-sends the same picture. That is the whole request in this
            // route, so unlike the OCR one there is nothing cheaper to reuse.
            window.RetranslateHandler = () => RunVisionAsync(window);

            var translator = CreateVisionTranslator();
            if (!translator.IsConfigured)
            {
                window.SetTranslationResult(TranslationOutcome.Error(
                    TranslationStatus.NotConfigured,
                    "「看图直翻」还没配置好。右键托盘图标 → 设置 → 看图直翻，"
                    + "填上接口地址、API Key 和模型名，或者在那里切回「先识别文字再翻译」。"));
                return;
            }

            var wantOriginal = Config.Vision.IncludeOriginal;

            var request = new TranslationRequest("", "", Config.TargetLanguage, Config.Vision.ExtraPrompt)
            {
                Image = window.Image,
                WantOriginal = wantOriginal,
            };

            // The reply carries the transcription after a marker, so the streamed text has
            // to be cut at the marker before it reaches the popup — otherwise the original
            // scrolls past as if it were part of the translation.
            IProgress<string>? progress = Config.Vision.StreamTranslation
                ? new Progress<string>(partial =>
                    window.ShowPartialTranslation(
                        wantOriginal ? VisionReply.TranslationSoFar(partial) : partial))
                : null;

            var result = await translator.TranslateAsync(request, progress, window.Lifetime);

            if (window.Lifetime.IsCancellationRequested) return;

            var original = "";
            if (wantOriginal && result.IsSuccess)
            {
                string translation;
                (translation, original) = VisionReply.Split(result.Text);
                result = result.WithText(translation);
            }

            window.SetLateOriginal(original);
            window.SetTranslationResult(result);

            UsageStore.Add(UsageStore.RouteVision, result.PromptTokens, result.CompletionTokens);
            RememberVisionTranslation(result, original);
        }
        catch (OperationCanceledException)
        {
            // The popup was closed; nothing to report.
        }
        catch (Exception ex)
        {
            Log.Error("看图直翻出错", ex);
            if (!window.Lifetime.IsCancellationRequested)
            {
                window.SetTranslationResult(TranslationOutcome.Error(
                    TranslationStatus.Failed, $"{ex.Message}（详情见日志）", ex.ToString()));
            }
        }
    }

    private static void RememberTranslation(OcrOutcome ocr, TranslationOutcome result)
    {
        if (!Config.OpenAi.KeepHistory) return;
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text)) return;

        HistoryStore.Add(new TranslationRecord
        {
            SourceLanguage = OcrLanguages.DisplayFor(ocr.LanguageTag),
            Original = ocr.Text,
            Translation = result.Text,
        });
    }

    /// <summary>
    /// The original is whatever the model transcribed, or empty when that was switched off.
    /// The label goes in either way so the history window can tell at a glance which route
    /// produced an entry.
    /// </summary>
    private static void RememberVisionTranslation(TranslationOutcome result, string original)
    {
        if (!Config.Vision.KeepHistory) return;
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text)) return;

        HistoryStore.Add(new TranslationRecord
        {
            SourceLanguage = "看图",
            Original = original,
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

        if (ClipboardHelper.TrySetText(text))
            _tray?.Notify("已复制", record.Summary(60));
        else
            _tray?.Notify("复制失败", "剪贴板被别的程序占着，过一秒再试。", ToolTipIcon.Warning);
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
        new OpenAiCompatibleTranslator(Config.OpenAi, vision: false);

    /// <summary>Same contract as <see cref="CreateTranslator"/>, for 看图翻译.</summary>
    public static ITranslator CreateVisionTranslator() =>
        new OpenAiCompatibleTranslator(Config.Vision, vision: true);

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

        if (_overlayHotkeyProblem is not null)
            _settings.SetSnapshotHotkeyStatus(_overlayHotkeyProblem, ok: false);
        else if (_overlayHotkey?.Current is { } snapshot)
            _settings.SetSnapshotHotkeyStatus($"快捷键 {snapshot} 已生效。", ok: true);
        else
            _settings.SetSnapshotHotkeyStatus("整屏翻译快捷键已关闭，托盘菜单里仍然可以用。", ok: true);
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

        // After Config is swapped in, because it reads the new value from there. A failure
        // is reported through the settings window's own status line, not by refusing the
        // save: everything else the user just edited has already been written.
        RegisterOverlayHotkey();

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
        _overlayHotkey?.Dispose();
        _tray?.Dispose();
        _guard?.Dispose();
        base.OnExit(e);
    }
}
