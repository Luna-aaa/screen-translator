using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTranslator.Config;
using ScreenTranslator.Hotkey;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;
using ScreenTranslator.Translate;
// Both WinForms and WPF are referenced; pin the WPF variants of the clashing types.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
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

    private readonly AppConfig _working;
    private HotkeySpec _pendingHotkey;
    private bool _loading;

    /// <summary>
    /// Set by the app. Applies the config for real (registers the hotkey, writes the
    /// registry, persists to disk) and returns null on success or a user-facing
    /// message on failure.
    /// </summary>
    public Func<AppConfig, string?>? SaveHandler { get; set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;

        _working = config.Clone();
        _pendingHotkey = HotkeySpec.TryParse(_working.Hotkey, out var parsed) ? parsed! : HotkeySpec.Default;

        LoadFromConfig();
    }

    // ---------------------------------------------------------------- loading

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            HotkeyBox.Text = _pendingHotkey.ToString();
            AutoStartCheck.IsChecked = _working.AutoStart;
            SaveCapturesCheck.IsChecked = _working.SaveCaptures;

            CaptureDirBox.Text = _working.CaptureDirectory;
            UpdateCaptureDirHint();
            DataDirText.Text = $"配置和日志：{Paths.DataDir}";

            PresetCombo.ItemsSource = ServicePresets.All;
            PresetCombo.SelectedItem = ServicePresets.Find(_working.OpenAi.Preset);
            UpdatePresetHint();

            BaseUrlBox.Text = _working.OpenAi.BaseUrl;
            ModelBox.Text = _working.OpenAi.Model;
            ExtraPromptBox.Text = _working.OpenAi.ExtraPrompt;
            TimeoutBox.Text = _working.RequestTimeoutSeconds.ToString();
            ApiKeyBox.Password = SecureStore.Unprotect(_working.OpenAi.ApiKeyProtected);

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
            VersionText.Text = $"版本 {version?.ToString(3) ?? "0.1.0"}　·　阶段 0（骨架）";
            AboutPathsText.Text =
                $"程序：{Paths.ExecutablePath}\n配置：{Paths.ConfigFile}\n日志：{Paths.LogDir}"
                + $"\n截图：{Paths.ResolveCaptureDir(_working.CaptureDirectory)}";
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
        HotkeyStatusText.Foreground = ok
            ? (Brush)FindResource("Ok")
            : (Brush)FindResource("Danger");
    }

    // ------------------------------------------------------------ hotkey edit

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Alt combinations arrive as Key.System; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            HotkeyBox.Text = _pendingHotkey.ToString();
            SetStatus("已取消修改");
            return;
        }

        // Ignore the modifier keys themselves — wait for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None or Key.ImeProcessed)
        {
            return;
        }

        var mods = HotkeyModifiers.None;
        var pressed = Keyboard.Modifiers;
        if (pressed.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (pressed.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (pressed.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (pressed.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        var candidate = new HotkeySpec(mods, vk);
        if (!candidate.IsUsable)
        {
            SetStatus("这个组合不能用：至少要带 Ctrl / Alt / Win 其中之一（或者单独一个 F1–F12）。", isError: true);
            return;
        }

        _pendingHotkey = candidate;
        HotkeyBox.Text = candidate.ToString();
        SetStatus($"快捷键将改为 {candidate}，点「保存」生效。");
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingHotkey = HotkeySpec.Default;
        HotkeyBox.Text = _pendingHotkey.ToString();
        SetStatus($"已恢复为默认 {_pendingHotkey}，点「保存」生效。");
    }

    // ------------------------------------------------------------- tab events

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePresetHint();
        if (_loading) return;

        if (PresetCombo.SelectedItem is ServicePreset preset && preset.Id != ServicePresets.Custom.Id)
        {
            // Overwrite endpoint + model, but never the key: switching vendors is the
            // whole point of the preset, and the key belongs to whichever vendor it came from.
            BaseUrlBox.Text = preset.BaseUrl;
            ModelBox.Text = preset.Model;
        }
    }

    private void UpdatePresetHint()
    {
        PresetHintText.Text = PresetCombo.SelectedItem is ServicePreset p ? p.SignupHint : "";
    }

    private void SourceLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateCandidatePanelState();

    private void UpdateCandidatePanelState()
    {
        var isAuto = (SourceLanguageCombo.SelectedItem as LanguageOption)?.Tag == AutoLanguage.Tag;
        CandidatePanel.IsEnabled = isAuto;
        CandidatePanel.Opacity = isAuto ? 1.0 : 0.45;
    }

    private void ShowKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
            ApiKeyPlainBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyPlainBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyPlainBox.Visibility = Visibility.Collapsed;
        }
    }

    private string CurrentApiKey =>
        ShowKeyCheck.IsChecked == true ? ApiKeyPlainBox.Text : ApiKeyBox.Password;

    /// <summary>
    /// Builds a translator from what is currently on screen rather than what was last
    /// saved, so both buttons validate the settings the user is actually looking at.
    /// </summary>
    private OpenAiCompatibleTranslator BuildProbeTranslator()
    {
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout)) timeout = 30;

        var probe = new OpenAiSettings
        {
            BaseUrl = BaseUrlBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            ApiKeyProtected = SecureStore.Protect(CurrentApiKey),
        };

        return new OpenAiCompatibleTranslator(probe, timeout);
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        FetchModelsButton.IsEnabled = false;
        FetchModelsButton.Content = "拉取中…";
        SetStatus("正在向服务商查询可用模型…");

        try
        {
            var outcome = await BuildProbeTranslator().ListModelsAsync();

            if (!outcome.IsSuccess)
            {
                SetStatus(outcome.Message, isError: true);
                return;
            }

            // Setting ItemsSource clears the editable text, so put the current value back.
            var current = ModelBox.Text;
            ModelBox.ItemsSource = outcome.Models;
            ModelBox.Text = current;

            ModelHintText.Text = $"服务商当前提供 {outcome.Models.Count} 个模型，点开下拉框选一个。";
            SetStatus($"{outcome.Message} 点开「模型」下拉框选择。");
            ModelBox.IsDropDownOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error("拉取模型列表出错", ex);
            SetStatus($"拉取失败：{ex.Message}", isError: true);
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
            FetchModelsButton.Content = "拉取模型";
        }
    }

    /// <summary>
    /// Tests exactly what is on screen right now, not what was last saved — otherwise the
    /// button would validate stale settings and the user would chase a phantom.
    /// </summary>
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        var key = CurrentApiKey;

        if (baseUrl.Length == 0 || model.Length == 0 || key.Length == 0)
        {
            SetTestResult("接口地址、API Key、模型三样都要填。", isError: true);
            return;
        }

        TestConnectionButton.IsEnabled = false;
        TestConnectionButton.Content = "测试中…";
        SetTestResult("正在发送请求…");

        try
        {
            var outcome = await BuildProbeTranslator().TestAsync();

            if (outcome.IsSuccess)
            {
                SetTestResult($"连接成功，用时 {outcome.ElapsedMs} ms。返回：{outcome.Text}");
            }
            else
            {
                SetTestResult(outcome.Message, isError: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("测试连接出错", ex);
            SetTestResult($"测试出错：{ex.Message}", isError: true);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
            TestConnectionButton.Content = "测试连接";
        }
    }

    private void SetTestResult(string message, bool isError = false)
    {
        TestResultText.Text = message;
        TestResultText.Foreground = isError ? (Brush)FindResource("Danger") : (Brush)FindResource("Muted");
    }

    // ------------------------------------------------------- OCR language packs

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

    private void UpdateCaptureDirHint()
    {
        var resolved = Paths.ResolveCaptureDir(CaptureDirBox.Text);
        CaptureDirText.Text = string.IsNullOrWhiteSpace(CaptureDirBox.Text)
            ? $"留空则用默认位置：{resolved}"
            : $"保存到：{resolved}";
    }

    private void BrowseCaptureDir_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择截图保存位置",
            UseDescriptionForTitle = true,
            SelectedPath = Paths.ResolveCaptureDir(CaptureDirBox.Text),
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            CaptureDirBox.Text = dialog.SelectedPath;
            UpdateCaptureDirHint();
            SetStatus("截图位置已改，点「保存」生效。");
        }
    }

    private void OpenCaptureDir_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Paths.ResolveCaptureDir(CaptureDirBox.Text));

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
            error = "快捷键无效，请重新设置。";
            return false;
        }

        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 5 || timeout > 300)
        {
            error = "超时请填 5 到 300 之间的整数（秒）。";
            return false;
        }

        var baseUrl = BaseUrlBox.Text.Trim();
        if (baseUrl.Length > 0 &&
            !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
              && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            error = "接口地址要是一个完整网址，以 http:// 或 https:// 开头。";
            return false;
        }

        var captureDir = CaptureDirBox.Text.Trim();
        if (captureDir.Length > 0)
        {
            // Fail here rather than silently at capture time, when the screenshot would
            // already have been taken.
            if (!Path.IsPathFullyQualified(captureDir))
            {
                error = "截图位置要填完整路径，比如 D:\\ScreenTranslator\\Captures。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(captureDir);
            }
            catch (Exception ex)
            {
                error = $"这个截图位置用不了：{ex.Message}";
                return false;
            }
        }

        config.Hotkey = _pendingHotkey.ToString();
        config.AutoStart = AutoStartCheck.IsChecked == true;
        config.SaveCaptures = SaveCapturesCheck.IsChecked == true;
        config.CaptureDirectory = captureDir;
        config.RequestTimeoutSeconds = timeout;

        config.OpenAi.Preset = (PresetCombo.SelectedItem as ServicePreset)?.Id ?? ServicePresets.Custom.Id;
        config.OpenAi.BaseUrl = baseUrl;
        config.OpenAi.Model = ModelBox.Text.Trim();
        config.OpenAi.ExtraPrompt = ExtraPromptBox.Text.Trim();
        config.OpenAi.ApiKeyProtected = SecureStore.Protect(CurrentApiKey);

        config.OcrSourceLanguage = (SourceLanguageCombo.SelectedItem as LanguageOption)?.Tag ?? "auto";

        var candidates = new List<string>();
        if (LangEnCheck.IsChecked == true) candidates.Add("en-US");
        if (LangJaCheck.IsChecked == true) candidates.Add("ja-JP");
        if (LangZhCheck.IsChecked == true) candidates.Add("zh-Hans-CN");
        if (LangKoCheck.IsChecked == true) candidates.Add("ko-KR");
        if (candidates.Count == 0 && config.OcrSourceLanguage == "auto")
        {
            error = "自动识别至少要勾选一种候选语言。";
            return false;
        }
        config.OcrCandidateLanguages = candidates;

        return true;
    }

    private static void CopyInto(AppConfig from, AppConfig to)
    {
        to.Hotkey = from.Hotkey;
        to.AutoStart = from.AutoStart;
        to.SaveCaptures = from.SaveCaptures;
        to.CaptureDirectory = from.CaptureDirectory;
        to.RequestTimeoutSeconds = from.RequestTimeoutSeconds;
        to.OcrSourceLanguage = from.OcrSourceLanguage;
        to.OcrCandidateLanguages = new List<string>(from.OcrCandidateLanguages);
        to.ActiveTranslator = from.ActiveTranslator;
        to.TargetLanguage = from.TargetLanguage;
        to.OpenAi = from.OpenAi.Clone();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? (Brush)FindResource("Danger") : (Brush)FindResource("Muted");
    }
}
