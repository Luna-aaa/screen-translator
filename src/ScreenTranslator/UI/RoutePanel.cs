using System.Windows;
using System.Windows.Controls;
using ScreenTranslator.Config;
using ScreenTranslator.Translate;
// Both WinForms and WPF are referenced; pin the WPF variants of the clashing types.
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ScreenTranslator.UI;

/// <summary>
/// Binds one route's settings to the controls that edit them.
///
/// There are three routes on screen now, each with the same nine or ten fields. Written
/// out three times that is three places to forget a field — and the failure mode for a
/// forgotten field is silent: the value simply rolls back on the next save, with no error.
/// One binder used three times cannot do that.
/// </summary>
internal sealed class RoutePanel
{
    private readonly Func<string> _defaultDir;

    /// <summary>
    /// Which preset's defaults are already reflected in the boxes.
    ///
    /// Guards against a SelectionChanged that is not a user choice. Switching settings tabs
    /// makes WPF tear down and rebuild the page, and the ComboBox's selection blips through
    /// null and back on the way — which used to look exactly like "the user picked a
    /// vendor" and silently overwrote the address and model with the preset's defaults.
    /// The user then saved, and their model name was gone with nothing on screen to say so.
    /// </summary>
    private string _appliedPreset = "";

    public required ComboBox PresetCombo { get; init; }
    public required TextBlock PresetHint { get; init; }
    public required IReadOnlyList<ServicePreset> Presets { get; init; }

    public required TextBox BaseUrlBox { get; init; }
    public required PasswordBox KeyBox { get; init; }
    public required TextBox KeyPlainBox { get; init; }
    public required CheckBox ShowKeyCheck { get; init; }
    public required ComboBox ModelBox { get; init; }
    public required TextBox TimeoutBox { get; init; }
    public required TextBox ExtraBox { get; init; }
    public required TextBox DirBox { get; init; }
    public required TextBlock DirText { get; init; }

    /// <summary>Null on the whole-screen route, which saves on demand rather than every time.</summary>
    public CheckBox? SaveCheck { get; init; }

    /// <summary>Null on the text route, which sends no image.</summary>
    public TextBox? MaxEdgeBox { get; init; }

    /// <summary>Only 看图翻译 has this: it is the one route with no original of its own.</summary>
    public CheckBox? IncludeOriginalCheck { get; init; }

    /// <summary>Null on the whole-screen route, which has no popup.</summary>
    public CheckBox? StreamCheck { get; init; }
    public ComboBox? ThemeCombo { get; init; }
    public CheckBox? HistoryCheck { get; init; }

    /// <summary>Human name for this route, for error messages.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether this route puts the picture in the request.</summary>
    public required bool Vision { get; init; }

    public RoutePanel(Func<string> defaultDir) => _defaultDir = defaultDir;

    public string CurrentKey => ShowKeyCheck.IsChecked == true ? KeyPlainBox.Text : KeyBox.Password;

    // ------------------------------------------------------------------- load

    public void Load(RouteSettings settings)
    {
        PresetCombo.ItemsSource = Presets;
        PresetCombo.SelectedItem = Presets.FirstOrDefault(
            p => string.Equals(p.Id, settings.Preset, StringComparison.OrdinalIgnoreCase)) ?? Presets[^1];
        _appliedPreset = (PresetCombo.SelectedItem as ServicePreset)?.Id ?? "custom";
        UpdatePresetHint();

        BaseUrlBox.Text = settings.BaseUrl;
        ModelBox.Text = settings.Model;
        KeyBox.Password = SecureStore.Unprotect(settings.ApiKeyProtected);
        TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        ExtraBox.Text = settings.ExtraPrompt;
        DirBox.Text = settings.CaptureDirectory;

        if (MaxEdgeBox is not null) MaxEdgeBox.Text = settings.MaxImageEdge.ToString();
        if (SaveCheck is not null) SaveCheck.IsChecked = settings.SaveCaptures;

        if (settings is VisionSettings vision && IncludeOriginalCheck is not null)
            IncludeOriginalCheck.IsChecked = vision.IncludeOriginal;

        if (settings is PopupRouteSettings popup)
        {
            if (StreamCheck is not null) StreamCheck.IsChecked = popup.StreamTranslation;
            if (HistoryCheck is not null) HistoryCheck.IsChecked = popup.KeepHistory;
            if (ThemeCombo is not null)
            {
                ThemeCombo.ItemsSource = PopupThemes.All;
                ThemeCombo.SelectedItem = PopupThemes.Find(popup.PopupTheme);
            }
        }

        UpdateDirHint();
    }

    // ------------------------------------------------------------------- save

    /// <returns>Null when everything is valid, or a message naming the field and the route.</returns>
    public string? Validate()
    {
        var url = BaseUrlBox.Text.Trim();
        if (url.Length > 0 &&
            !(Uri.TryCreate(url, UriKind.Absolute, out var uri)
              && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            return $"「{DisplayName}」的接口地址要是一个完整网址，以 http:// 或 https:// 开头。";
        }

        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout is < 5 or > 300)
            return $"「{DisplayName}」的超时请填 5 到 300 之间的整数（秒）。";

        if (MaxEdgeBox is not null
            && (!int.TryParse(MaxEdgeBox.Text.Trim(), out var edge) || edge is < 640 or > 3200))
        {
            return $"「{DisplayName}」的图片最大边长请填 640 到 3200 之间的整数（像素）。";
        }

        var dir = DirBox.Text.Trim();
        if (dir.Length > 0)
        {
            // Fail here rather than silently at capture time, when the screenshot would
            // already have been taken.
            if (!System.IO.Path.IsPathFullyQualified(dir))
                return $"「{DisplayName}」的保存位置要填完整路径，比如 D:\\ScreenTranslator\\框选翻译。";

            try
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                return $"「{DisplayName}」的保存位置用不了：{ex.Message}";
            }
        }

        return null;
    }

    /// <summary>Only call after <see cref="Validate"/> has returned null.</summary>
    public void WriteTo(RouteSettings settings)
    {
        settings.Preset = (PresetCombo.SelectedItem as ServicePreset)?.Id ?? "custom";
        settings.BaseUrl = BaseUrlBox.Text.Trim();
        settings.Model = ModelBox.Text.Trim();
        settings.ApiKeyProtected = SecureStore.Protect(CurrentKey);
        settings.ExtraPrompt = ExtraBox.Text.Trim();
        settings.TimeoutSeconds = int.Parse(TimeoutBox.Text.Trim());
        settings.CaptureDirectory = DirBox.Text.Trim();

        if (MaxEdgeBox is not null) settings.MaxImageEdge = int.Parse(MaxEdgeBox.Text.Trim());
        if (SaveCheck is not null) settings.SaveCaptures = SaveCheck.IsChecked == true;

        if (settings is VisionSettings vision && IncludeOriginalCheck is not null)
            vision.IncludeOriginal = IncludeOriginalCheck.IsChecked == true;

        if (settings is PopupRouteSettings popup)
        {
            if (StreamCheck is not null) popup.StreamTranslation = StreamCheck.IsChecked == true;
            if (HistoryCheck is not null) popup.KeepHistory = HistoryCheck.IsChecked == true;
            if (ThemeCombo is not null)
                popup.PopupTheme = (ThemeCombo.SelectedItem as PopupTheme)?.Id ?? PopupThemes.Dark.Id;
        }
    }

    // ----------------------------------------------------------------- probes

    /// <summary>
    /// Builds a translator from what is on screen right now rather than what was last
    /// saved, so both buttons validate the settings the user is actually looking at.
    /// </summary>
    public OpenAiCompatibleTranslator BuildProbe()
    {
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout)) timeout = 30;
        if (MaxEdgeBox is null || !int.TryParse(MaxEdgeBox.Text.Trim(), out var edge)) edge = 1600;

        RouteSettings probe = Vision
            ? new SnapshotSettings()
            : new OpenAiSettings();

        probe.BaseUrl = BaseUrlBox.Text.Trim();
        probe.Model = ModelBox.Text.Trim();
        probe.ApiKeyProtected = SecureStore.Protect(CurrentKey);
        probe.TimeoutSeconds = Math.Clamp(timeout, 5, 300);
        probe.MaxImageEdge = Math.Clamp(edge, 640, 3200);

        return new OpenAiCompatibleTranslator(probe, Vision);
    }

    /// <summary>True when the three things a request needs are all filled in.</summary>
    public bool IsFilledIn =>
        BaseUrlBox.Text.Trim().Length > 0 && ModelBox.Text.Trim().Length > 0 && CurrentKey.Length > 0;

    // ------------------------------------------------------------------ events

    public void OnPresetChanged(bool loading)
    {
        UpdatePresetHint();
        if (loading) return;

        // A transient null during a rebuild is not a choice.
        if (PresetCombo.SelectedItem is not ServicePreset preset) return;

        // Landing back on the preset already in effect is not a choice either.
        if (string.Equals(preset.Id, _appliedPreset, StringComparison.OrdinalIgnoreCase)) return;
        _appliedPreset = preset.Id;

        if (preset.Id == "custom") return;

        // Overwrite endpoint + model, but never the key: switching vendors is the whole
        // point of the preset, and the key belongs to whichever vendor it came from.
        BaseUrlBox.Text = preset.BaseUrl;
        ModelBox.Text = preset.Model;
    }

    public void UpdatePresetHint() =>
        PresetHint.Text = PresetCombo.SelectedItem is ServicePreset p ? p.SignupHint : "";

    public void OnShowKeyChanged()
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            KeyPlainBox.Text = KeyBox.Password;
            KeyPlainBox.Visibility = Visibility.Visible;
            KeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            KeyBox.Password = KeyPlainBox.Text;
            KeyBox.Visibility = Visibility.Visible;
            KeyPlainBox.Visibility = Visibility.Collapsed;
        }
    }

    public string ResolvedDir => Infrastructure.Paths.Resolve(DirBox.Text, _defaultDir());

    public void UpdateDirHint() =>
        DirText.Text = string.IsNullOrWhiteSpace(DirBox.Text)
            ? $"留空则用默认位置：{ResolvedDir}"
            : $"保存到：{ResolvedDir}";

    public void ShowModelList(IReadOnlyList<string> models, Button button)
    {
        // Setting ItemsSource clears the editable text, so put the current value back.
        var current = ModelBox.Text;
        ModelBox.ItemsSource = models;
        ModelBox.Text = current;
        ModelBox.IsDropDownOpen = true;
    }
}
