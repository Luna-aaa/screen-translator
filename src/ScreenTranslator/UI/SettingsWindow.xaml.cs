using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTranslator.Config;
using ScreenTranslator.History;
using ScreenTranslator.Hotkey;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;
// Both WinForms and WPF are referenced; pin the WPF variants of the clashing types.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace ScreenTranslator.UI;

public partial class SettingsWindow : Window
{
    private sealed record LanguageOption(string Tag, string Display)
    {
        public override string ToString() => Display;
    }

    private static readonly LanguageOption AutoLanguage = new("auto", "自动识别（在下面勾选的语言里挑）");

    private static readonly LanguageOption[] SourceLanguages =
    {
        AutoLanguage,
        new("en-US", "锁定为英文"),
        new("ja-JP", "锁定为日文"),
        new("zh-Hans-CN", "锁定为中文"),
        new("ko-KR", "锁定为韩文"),
    };

    private sealed record PipelineOption(string Id, string Display, string Hint)
    {
        public override string ToString() => Display;
    }

    private static readonly PipelineOption[] PipelineOptions =
    {
        new(Pipelines.Classic, "识文翻译（默认）",
            "Windows 自带的文字识别先把字读出来，再交给左边配置的模型翻译。识别在本机做，"
            + "一块区域里只会按一种语言来读。"),
        new(Pipelines.Vision, "看图翻译（需要会看图的模型）",
            "把框选的那张图直接发给右边配置的模型，由它一边看一边翻，跳过 Windows 的文字识别。"
            + "花体字、彩色背景、竖排日文会准很多，一张图里有好几种语言也能一起翻。"
            + "代价是每次都要联网发图，比纯文字略贵略慢。"),
    };

    private readonly AppConfig _working;
    private HotkeySpec _pendingHotkey;

    /// <summary>Null means the snapshot hotkey is deliberately switched off, not unset.</summary>
    private HotkeySpec? _pendingSnapshotHotkey;

    private RoutePanel _classic = null!;
    private RoutePanel _vision = null!;
    private RoutePanel _snapshot = null!;

    private bool _loading;

    /// <summary>
    /// Set by the app. Applies the config for real (registers the hotkeys, writes the
    /// registry, persists to disk) and returns null on success or a user-facing
    /// message on failure.
    /// </summary>
    public Func<AppConfig, string?>? SaveHandler { get; set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;

        _working = config.Clone();

        // Must match the app's own rule (App.RegisterStartupHotkey), including IsUsable.
        // Accepting a parseable-but-unusable combo here would show the user one hotkey
        // while the app runs another, and then reject every save - including saves of
        // unrelated settings like the API key - with an error about a field they never touched.
        _pendingHotkey = HotkeySpec.TryParse(_working.Hotkey, out var parsed) && parsed.IsUsable
            ? parsed
            : HotkeySpec.Default;

        _pendingSnapshotHotkey =
            HotkeySpec.TryParse(_working.OverlayHotkey, out var snapshot) && snapshot.IsUsable
                ? snapshot
                : null;

        BuildRoutePanels();
        LoadFromConfig();
    }

    private void BuildRoutePanels()
    {
        _classic = new RoutePanel(() => Paths.DefaultCropDir)
        {
            DisplayName = "识文翻译",
            Vision = false,
            Presets = ServicePresets.All,
            PresetCombo = ClassicPresetCombo,
            PresetHint = ClassicPresetHint,
            BaseUrlBox = ClassicBaseUrlBox,
            KeyBox = ClassicKeyBox,
            KeyPlainBox = ClassicKeyPlainBox,
            ShowKeyCheck = ClassicShowKey,
            ModelBox = ClassicModelBox,
            TimeoutBox = ClassicTimeoutBox,
            ExtraBox = ClassicExtraBox,
            DirBox = ClassicDirBox,
            DirText = ClassicDirText,
            SaveCheck = ClassicSaveCheck,
            StreamCheck = ClassicStreamCheck,
            ThemeCombo = ClassicThemeCombo,
            HistoryCheck = ClassicHistoryCheck,
        };

        _vision = new RoutePanel(() => Paths.DefaultCropDir)
        {
            DisplayName = "看图翻译",
            Vision = true,
            Presets = VisionPresets.All,
            PresetCombo = VisionPresetCombo,
            PresetHint = VisionPresetHint,
            BaseUrlBox = VisionBaseUrlBox,
            KeyBox = VisionKeyBox,
            KeyPlainBox = VisionKeyPlainBox,
            ShowKeyCheck = VisionShowKey,
            ModelBox = VisionModelBox,
            TimeoutBox = VisionTimeoutBox,
            ExtraBox = VisionExtraBox,
            DirBox = VisionDirBox,
            DirText = VisionDirText,
            SaveCheck = VisionSaveCheck,
            MaxEdgeBox = VisionMaxEdgeBox,
            IncludeOriginalCheck = VisionIncludeOriginalCheck,
            StreamCheck = VisionStreamCheck,
            ThemeCombo = VisionThemeCombo,
            HistoryCheck = VisionHistoryCheck,
        };

        _snapshot = new RoutePanel(() => Paths.DefaultSnapshotDir)
        {
            DisplayName = "全屏翻译",
            Vision = true,
            Presets = VisionPresets.All,
            PresetCombo = SnapPresetCombo,
            PresetHint = SnapPresetHint,
            BaseUrlBox = SnapBaseUrlBox,
            KeyBox = SnapKeyBox,
            KeyPlainBox = SnapKeyPlainBox,
            ShowKeyCheck = SnapShowKey,
            ModelBox = SnapModelBox,
            TimeoutBox = SnapTimeoutBox,
            ExtraBox = SnapExtraBox,
            DirBox = SnapDirBox,
            DirText = SnapDirText,
            MaxEdgeBox = SnapMaxEdgeBox,
        };
    }

    // ---------------------------------------------------------------- loading

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            HotkeyBox.Text = _pendingHotkey.ToString();
            SnapshotHotkeyBox.Text = _pendingSnapshotHotkey?.ToString() ?? "（不用）";
            AutoStartCheck.IsChecked = _working.AutoStart;
            DataDirText.Text = $"配置和日志：{Paths.DataDir}";

            PipelineCombo.ItemsSource = PipelineOptions;
            PipelineCombo.SelectedItem = PipelineOptions.FirstOrDefault(
                    o => string.Equals(o.Id, _working.Pipeline, StringComparison.OrdinalIgnoreCase))
                ?? PipelineOptions[0];
            UpdatePipelineHint();

            _classic.Load(_working.OpenAi);
            _vision.Load(_working.Vision);
            _snapshot.Load(_working.Snapshot);

            UpdateHistoryHint();

            SourceLanguageCombo.ItemsSource = SourceLanguages;
            SourceLanguageCombo.SelectedItem =
                SourceLanguages.FirstOrDefault(l => l.Tag.Equals(_working.OcrSourceLanguage, StringComparison.OrdinalIgnoreCase))
                ?? AutoLanguage;

            var candidates = _working.OcrCandidateLanguages;
            LangEnCheck.IsChecked = Has(candidates, "en");
            LangJaCheck.IsChecked = Has(candidates, "ja");
            LangZhCheck.IsChecked = Has(candidates, "zh");
            LangKoCheck.IsChecked = Has(candidates, "ko");
            UpdateCandidatePanelState();
            RefreshLanguagePacks();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"版本 {version?.ToString(3) ?? "0.1.0"}　·　阶段 6（全屏原位覆盖）";
            AboutPathsText.Text =
                $"程序：{Paths.ExecutablePath}\n配置：{Paths.ConfigFile}\n日志：{Paths.LogDir}"
                + $"\n框选截图：{_classic.ResolvedDir}\n全屏译文图：{_snapshot.ResolvedDir}";
        }
        finally
        {
            _loading = false;
        }

        static bool Has(List<string> list, string prefix) =>
            list.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mirrors a change made from the tray menu while this window is open, without
    /// disturbing whatever else the user is part-way through editing.
    /// </summary>
    public void SetAutoStartChecked(bool value)
    {
        _loading = true;
        AutoStartCheck.IsChecked = value;
        _loading = false;
    }

    /// <summary>Called by the app so the window can report whether the hotkey is live.</summary>
    public void SetHotkeyStatus(string message, bool ok)
    {
        HotkeyStatusText.Text = message;
        HotkeyStatusText.Foreground = ok ? (Brush)FindResource("Ok") : (Brush)FindResource("Danger");
    }

    /// <summary>Same, for the whole-screen snapshot hotkey.</summary>
    public void SetSnapshotHotkeyStatus(string message, bool ok)
    {
        SnapshotHotkeyStatusText.Text = message;
        SnapshotHotkeyStatusText.Foreground = ok ? (Brush)FindResource("Ok") : (Brush)FindResource("Danger");
    }

    /// <summary>
    /// Stops the wheel from changing a ComboBox's value while the user is only scrolling
    /// the page past it. Silently switching the model this way produced a translation
    /// failure minutes later, with nothing on screen connecting the two.
    /// </summary>
    private void Combo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } combo) return;

        e.Handled = true;

        // Re-raise so the ScrollViewer above still scrolls; without this the page would
        // simply stop moving whenever the pointer crossed a dropdown.
        combo.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = combo,
        });
    }

    // ------------------------------------------------------------ hotkey edit

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var candidate = ReadHotkey(e, out var cancelled);
        if (cancelled)
        {
            HotkeyBox.Text = _pendingHotkey.ToString();
            SetStatus("已取消修改");
            return;
        }

        if (candidate is null) return;

        if (_pendingSnapshotHotkey is not null && candidate.ToString() == _pendingSnapshotHotkey.ToString())
        {
            SetStatus("这个组合已经给「全屏翻译」用了，换一个。", isError: true);
            return;
        }

        _pendingHotkey = candidate;
        HotkeyBox.Text = candidate.ToString();
        SetStatus($"框选翻译快捷键将改为 {candidate}，点「保存」生效。");
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingHotkey = HotkeySpec.Default;
        HotkeyBox.Text = _pendingHotkey.ToString();
        SetStatus($"已恢复为默认 {_pendingHotkey}，点「保存」生效。");
    }

    private void SnapshotHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var candidate = ReadHotkey(e, out var cancelled);
        if (cancelled)
        {
            SnapshotHotkeyBox.Text = _pendingSnapshotHotkey?.ToString() ?? "（不用）";
            SetStatus("已取消修改");
            return;
        }

        if (candidate is null) return;

        // The two hotkeys must differ, or the second registration fails at save time with
        // a message about a key the user can plainly see is free.
        if (candidate.ToString() == _pendingHotkey.ToString())
        {
            SetStatus("这个组合已经给「框选翻译」用了，换一个。", isError: true);
            return;
        }

        _pendingSnapshotHotkey = candidate;
        SnapshotHotkeyBox.Text = candidate.ToString();
        SetStatus($"全屏翻译快捷键将改为 {candidate}，点「保存」生效。");
    }

    private void ResetSnapshotHotkey_Click(object sender, RoutedEventArgs e)
    {
        _pendingSnapshotHotkey = HotkeySpec.TryParse("Ctrl+Alt+W", out var spec) ? spec : null;
        SnapshotHotkeyBox.Text = _pendingSnapshotHotkey?.ToString() ?? "（不用）";
        SetStatus("已恢复为默认 Ctrl+Alt+W，点「保存」生效。");
    }

    private void ClearSnapshotHotkey_Click(object sender, RoutedEventArgs e)
    {
        _pendingSnapshotHotkey = null;
        SnapshotHotkeyBox.Text = "（不用）";
        SetStatus("全屏翻译快捷键将被关掉（托盘菜单里仍然能用），点「保存」生效。");
    }

    /// <summary>
    /// Shared by both hotkey boxes. Returns null while the user is still holding only
    /// modifiers, or when the combination is not one Windows will register.
    /// </summary>
    private HotkeySpec? ReadHotkey(KeyEventArgs e, out bool cancelled)
    {
        cancelled = false;

        // Alt combinations arrive as Key.System; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { cancelled = true; return null; }

        // Ignore the modifier keys themselves — wait for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None or Key.ImeProcessed)
        {
            return null;
        }

        var mods = HotkeyModifiers.None;
        var pressed = Keyboard.Modifiers;
        if (pressed.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (pressed.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (pressed.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (pressed.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;

        var candidate = new HotkeySpec(mods, vk);
        if (!candidate.IsUsable)
        {
            SetStatus("这个组合不能用：至少要带 Ctrl / Alt / Win 其中之一（或者单独一个 F1–F12）。", isError: true);
            return null;
        }

        return candidate;
    }

    // ------------------------------------------------------------ route events

    private void PipelineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdatePipelineHint();

    /// <summary>
    /// Dims the column that is not in use rather than disabling it, so the other route can
    /// still be filled in and tested before switching over.
    /// </summary>
    private void UpdatePipelineHint()
    {
        var option = PipelineCombo.SelectedItem as PipelineOption ?? PipelineOptions[0];
        PipelineHintText.Text = option.Hint;

        var isVision = Pipelines.IsVision(option.Id);
        if (ClassicCard is not null) ClassicCard.Opacity = isVision ? 0.6 : 1.0;
        if (VisionCard is not null) VisionCard.Opacity = isVision ? 1.0 : 0.6;
    }

    private void ClassicPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _classic?.OnPresetChanged(_loading);

    private void VisionPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vision?.OnPresetChanged(_loading);

    private void SnapPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _snapshot?.OnPresetChanged(_loading);

    private void ClassicShowKey_Changed(object sender, RoutedEventArgs e) => _classic?.OnShowKeyChanged();
    private void VisionShowKey_Changed(object sender, RoutedEventArgs e) => _vision?.OnShowKeyChanged();
    private void SnapShowKey_Changed(object sender, RoutedEventArgs e) => _snapshot?.OnShowKeyChanged();

    private void ClassicDir_TextChanged(object sender, TextChangedEventArgs e) => _classic?.UpdateDirHint();
    private void VisionDir_TextChanged(object sender, TextChangedEventArgs e) => _vision?.UpdateDirHint();
    private void SnapDir_TextChanged(object sender, TextChangedEventArgs e) => _snapshot?.UpdateDirHint();

    private void ClassicFetchModels_Click(object sender, RoutedEventArgs e)
        => _ = FetchModelsAsync(_classic, ClassicFetchButton, ClassicModelHint);

    private void VisionFetchModels_Click(object sender, RoutedEventArgs e)
        => _ = FetchModelsAsync(_vision, VisionFetchButton, VisionModelHint);

    private void SnapFetchModels_Click(object sender, RoutedEventArgs e)
        => _ = FetchModelsAsync(_snapshot, SnapFetchButton, SnapModelHint);

    private async Task FetchModelsAsync(RoutePanel panel, Button button, TextBlock hint)
    {
        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "拉取中…";
        SetStatus($"正在向「{panel.DisplayName}」的服务商查询可用模型…");

        try
        {
            var outcome = await panel.BuildProbe().ListModelsAsync();

            if (!outcome.IsSuccess)
            {
                SetStatus(outcome.Message, isError: true);
                return;
            }

            panel.ShowModelList(outcome.Models, button);
            hint.Text = panel.Vision
                ? $"服务商当前提供 {outcome.Models.Count} 个模型。注意：里面大部分是不会看图的，要挑名字里带 vl / vision / 4v 的那种。"
                : $"服务商当前提供 {outcome.Models.Count} 个模型，点开下拉框选一个。";
            SetStatus($"{outcome.Message} 点开「模型」下拉框选择。");
        }
        catch (Exception ex)
        {
            Log.Error($"拉取模型列表出错（{panel.DisplayName}）", ex);
            SetStatus($"拉取失败：{ex.Message}", isError: true);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = label;
        }
    }

    private void ClassicTest_Click(object sender, RoutedEventArgs e)
        => _ = TestAsync(_classic, ClassicTestButton, ClassicTestText);

    private void VisionTest_Click(object sender, RoutedEventArgs e)
        => _ = TestAsync(_vision, VisionTestButton, VisionTestText);

    private void SnapTest_Click(object sender, RoutedEventArgs e)
        => _ = TestAsync(_snapshot, SnapTestButton, SnapTestText);

    /// <summary>
    /// Tests exactly what is on screen right now, not what was last saved — otherwise the
    /// button would validate stale settings and the user would chase a phantom.
    /// </summary>
    private async Task TestAsync(RoutePanel panel, Button button, TextBlock result)
    {
        if (!panel.IsFilledIn)
        {
            Report("接口地址、API Key、模型三样都要填。", isError: true);
            return;
        }

        button.IsEnabled = false;
        button.Content = "测试中…";
        Report(panel.Vision ? "正在发送一张测试图片…" : "正在发送请求…");

        try
        {
            var outcome = await panel.BuildProbe().TestAsync();

            if (!outcome.IsSuccess)
            {
                Report(outcome.Message, isError: true);
                return;
            }

            // The vision reply is quoted in full rather than summarised: reading it back is
            // how the user tells a model that actually looked at the picture from one that
            // guessed. A text-only model answers this prompt perfectly well without seeing.
            Report(panel.Vision
                ? $"成功，用时 {outcome.ElapsedMs} ms。图上写的是「Good morning. The sky is blue.」，"
                  + $"模型读回来的是：{outcome.Text}"
                : $"连接成功，用时 {outcome.ElapsedMs} ms。返回：{outcome.Text}");
        }
        catch (Exception ex)
        {
            Log.Error($"测试连接出错（{panel.DisplayName}）", ex);
            Report($"测试出错：{ex.Message}", isError: true);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = "测试连接";
        }

        void Report(string message, bool isError = false)
        {
            result.Text = message;
            result.Foreground = isError ? (Brush)FindResource("Danger") : (Brush)FindResource("Muted");
        }
    }

    // --------------------------------------------------------------- folders

    private void BrowseClassicDir_Click(object sender, RoutedEventArgs e) => Browse(_classic, "选择框选截图的保存位置");
    private void BrowseVisionDir_Click(object sender, RoutedEventArgs e) => Browse(_vision, "选择框选截图的保存位置");
    private void BrowseSnapDir_Click(object sender, RoutedEventArgs e) => Browse(_snapshot, "选择整屏译文图的保存位置");

    private void OpenClassicDir_Click(object sender, RoutedEventArgs e) => OpenFolder(_classic.ResolvedDir);
    private void OpenVisionDir_Click(object sender, RoutedEventArgs e) => OpenFolder(_vision.ResolvedDir);
    private void OpenSnapDir_Click(object sender, RoutedEventArgs e) => OpenFolder(_snapshot.ResolvedDir);

    private void Browse(RoutePanel panel, string title)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            SelectedPath = panel.ResolvedDir,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        panel.DirBox.Text = dialog.SelectedPath;
        panel.UpdateDirHint();
        SetStatus("保存位置已改，点「保存」生效。");
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e) => OpenFolder(Paths.DataDir);

    private void OpenLogDir_Click(object sender, RoutedEventArgs e) => OpenFolder(Paths.LogDir);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开目录失败：{path}", ex);
            SetStatus($"打不开目录：{ex.Message}", isError: true);
        }
    }

    // ------------------------------------------------------- OCR language packs

    private void SourceLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateCandidatePanelState();

    private void UpdateCandidatePanelState()
    {
        var isAuto = (SourceLanguageCombo.SelectedItem as LanguageOption)?.Tag == AutoLanguage.Tag;
        CandidatePanel.IsEnabled = isAuto;
        CandidatePanel.Opacity = isAuto ? 1.0 : 0.45;
    }

    private void RefreshLanguagePacks_Click(object sender, RoutedEventArgs e) => RefreshLanguagePacks();

    /// <summary>
    /// Rebuilds the installed/missing list. Re-queried rather than cached because the
    /// whole point is that the user can install a pack and see it appear without
    /// restarting the app.
    /// </summary>
    private void RefreshLanguagePacks()
    {
        LanguagePackPanel.Children.Clear();

        var installed = LanguagePackHelper.InstalledTags();
        var installedCount = 0;

        foreach (var language in OcrLanguages.All)
        {
            var isInstalled = installed.Any(i => OcrLanguages.TagsMatch(i, language.Tag));
            if (isInstalled) installedCount++;
            LanguagePackPanel.Children.Add(BuildLanguageRow(language, isInstalled));
        }

        LanguagePackHint.Text = installedCount == 0
            ? "系统里一个识别语言包都没有，文字识别无法工作。"
            : "安装需要管理员权限，会弹一次 UAC。装完点「刷新」就能看到，本程序不用重启。";
    }

    private UIElement BuildLanguageRow(OcrLanguage language, bool isInstalled)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = $"{language.DisplayName}　{language.Tag}",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Ink"),
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var status = new TextBlock
        {
            Text = isInstalled ? "已安装" : "未安装",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(isInstalled ? "Ok" : "Muted"),
        };
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);

        if (!isInstalled)
        {
            var install = new Button
            {
                Content = "安装",
                MinWidth = 62,
                Padding = new Thickness(10, 3, 10, 3),
                Style = (Style)FindResource("MinorButton"),
                Tag = language,
            };
            install.Click += InstallLanguage_Click;
            Grid.SetColumn(install, 2);
            grid.Children.Add(install);
        }

        return grid;
    }

    private async void InstallLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OcrLanguage language } button) return;

        button.IsEnabled = false;
        button.Content = "安装中…";
        SetStatus($"正在安装{language.DisplayName}识别语言包，请在弹出的窗口里允许管理员权限…");

        try
        {
            var (installed, message) = await LanguagePackHelper.TryInstallAsync(language);
            SetStatus(message, isError: !installed);
            RefreshLanguagePacks();
        }
        catch (Exception ex)
        {
            Log.Error("安装语言包失败", ex);
            SetStatus($"安装出错：{ex.Message}", isError: true);
            RefreshLanguagePacks();
        }
    }

    // ------------------------------------------------------------------ save

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConfig(out var config, out var error))
        {
            SetStatus(error, isError: true);
            return;
        }

        var failure = SaveHandler?.Invoke(config);
        if (failure is not null)
        {
            SetStatus(failure, isError: true);
            return;
        }

        // Re-sync the working copy so a second save starts from what was actually applied.
        CopyInto(config, _working);
        SetStatus($"已保存　{DateTime.Now:HH:mm:ss}");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private bool TryBuildConfig(out AppConfig config, out string error)
    {
        config = _working.Clone();
        error = "";

        if (!_pendingHotkey.IsUsable)
        {
            error = "框选翻译快捷键无效，请重新设置。";
            return false;
        }

        if (_pendingSnapshotHotkey is not null
            && _pendingSnapshotHotkey.ToString() == _pendingHotkey.ToString())
        {
            error = "两个快捷键不能设成同一个组合。";
            return false;
        }

        foreach (var panel in new[] { _classic, _vision, _snapshot })
        {
            if (panel.Validate() is { } problem)
            {
                error = problem;
                return false;
            }
        }

        config.Hotkey = _pendingHotkey.ToString();
        config.OverlayHotkey = _pendingSnapshotHotkey?.ToString() ?? "";
        config.AutoStart = AutoStartCheck.IsChecked == true;
        config.Pipeline = (PipelineCombo.SelectedItem as PipelineOption)?.Id ?? Pipelines.Classic;

        _classic.WriteTo(config.OpenAi);
        _vision.WriteTo(config.Vision);
        _snapshot.WriteTo(config.Snapshot);

        config.OcrSourceLanguage = (SourceLanguageCombo.SelectedItem as LanguageOption)?.Tag ?? "auto";

        var candidates = new List<string>();
        if (LangEnCheck.IsChecked == true) candidates.Add("en-US");
        if (LangJaCheck.IsChecked == true) candidates.Add("ja-JP");
        if (LangZhCheck.IsChecked == true) candidates.Add("zh-Hans-CN");
        if (LangKoCheck.IsChecked == true) candidates.Add("ko-KR");
        if (candidates.Count == 0 && config.OcrSourceLanguage == "auto"
            && !Pipelines.IsVision(config.Pipeline))
        {
            error = "自动识别至少要勾选一种候选语言。";
            return false;
        }
        config.OcrCandidateLanguages = candidates;

        return true;
    }

    /// <summary>
    /// Re-syncs the working copy after a successful save. Missing an entry here is the
    /// classic silent bug: the field reverts on the next save and nothing reports it.
    /// </summary>
    private static void CopyInto(AppConfig from, AppConfig to)
    {
        to.SchemaVersion = from.SchemaVersion;
        to.Hotkey = from.Hotkey;
        to.OverlayHotkey = from.OverlayHotkey;
        to.AutoStart = from.AutoStart;
        to.OcrSourceLanguage = from.OcrSourceLanguage;
        to.OcrCandidateLanguages = new List<string>(from.OcrCandidateLanguages);
        to.ActiveTranslator = from.ActiveTranslator;
        to.TargetLanguage = from.TargetLanguage;
        to.Pipeline = from.Pipeline;
        to.OpenAi = from.OpenAi.Clone();
        to.Vision = from.Vision.Clone();
        to.Snapshot = from.Snapshot.Clone();
    }

    // ----------------------------------------------------------------- history

    private void UpdateHistoryHint()
    {
        var count = HistoryStore.Count;
        HistoryHintText.Text = count == 0
            ? $"还没有记录。记录存在 {Paths.DataDir}\\history.json，最多保留 {HistoryStore.MaxEntries} 条。"
            : $"现在有 {count} 条，最多保留 {HistoryStore.MaxEntries} 条，存在 {Paths.DataDir}\\history.json。"
              + "上面两栏的勾只管「以后还记不记」，已有的要点「清空历史」。";
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        new HistoryWindow { Owner = this }.ShowDialog();
        UpdateHistoryHint();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryStore.Count == 0)
        {
            SetStatus("本来就没有记录。");
            return;
        }

        // Irreversible, so it asks - unlike everything else on this screen, which only
        // takes effect on save and can simply be re-edited.
        var answer = MessageBox.Show(this,
            $"确定要删掉全部 {HistoryStore.Count} 条翻译记录吗？删了就找不回来了。",
            "清空翻译历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        HistoryStore.Clear();
        UpdateHistoryHint();
        SetStatus("历史已清空。");
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? (Brush)FindResource("Danger") : (Brush)FindResource("Muted");
    }
}
