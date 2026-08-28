using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenTranslator.Infrastructure;
using ScreenTranslator.Ocr;
using ScreenTranslator.Translate;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Clipboard = System.Windows.Clipboard;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace ScreenTranslator.UI;

/// <summary>
/// The little panel that appears beside the captured region.
///
/// Its defining constraint: it must never take focus. The user is mid-sentence in a
/// document or mid-fight in a game, and a popup that activates would eat their keystrokes
/// or drop a fullscreen game to the desktop. So the window is WS_EX_NOACTIVATE, is shown
/// without activation, and is positioned with SetWindowPos rather than by WPF - which
/// also keeps it in the same physical-pixel space as the capture.
/// </summary>
public partial class ResultWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly Rectangle _selection;
    private DismissWatcher? _watcher;
    private IReadOnlyList<OcrLanguage> _missingLanguages = Array.Empty<OcrLanguage>();

    // Kept apart rather than as one "whatever is copyable right now" string: with a button
    // for each, both have to stay available at the same time.
    private string _translationText = "";
    private string _originalText = "";
    private string _sourceLabel = "";
    private bool _originalVisible;
    private bool _closing;
    private bool _pinned;
    private bool _streaming;
    private bool _settled;
    private bool _repositionQueued;

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// Handed out instead of <c>_lifetime.Token</c>: the CancellationTokenSource's Token
    /// property throws once the source is disposed, and callers legitimately read this
    /// after every await — including inside their own catch blocks — long after the popup
    /// has closed. A token captured up front keeps working.
    /// </summary>
    private readonly CancellationToken _lifetimeToken;

    /// <summary>Cancelled when the popup closes, so in-flight work stops with it.</summary>
    public CancellationToken Lifetime => _lifetimeToken;

    private bool _dragging;
    private bool _userMoved;
    private Point _dragCursorOrigin;
    private Point _dragWindowOrigin;

    /// <summary>Size of the bottom-right zone that resizes instead of moving, in DIP.</summary>
    private const int ResizeCornerDip = 22;

    /// <summary>
    /// Width of the frame around the edge that moves the window, in DIP. Everything inside
    /// it is content: the four-way cursor there was claiming the whole popup was a drag
    /// handle, which is both wrong-looking and at odds with selecting text.
    /// </summary>
    private const int DragBorderDip = 16;

    private bool _resizing;
    private bool _manualSize;
    private Point _resizeCursorOrigin;
    private Size _resizeWindowOrigin;

    private System.Windows.Threading.DispatcherTimer? _copyFeedback;
    private Action? _restoreCopyLabel;

    /// <summary>The captured crop. Owned by this window and disposed with it.</summary>
    public Bitmap Image { get; }

    /// <summary>Set by the app; re-runs recognition on <see cref="Image"/>.</summary>
    public Func<Task>? RetryHandler { get; set; }

    /// <summary>Set by the app; re-translates the text already recognized, skipping OCR.</summary>
    public Func<Task>? RetranslateHandler { get; set; }

    /// <summary>
    /// Set by the app; returns the screen rectangles this popup must not cover — the
    /// pinned popups already on screen. The app owns that list, so it is asked rather
    /// than told.
    /// </summary>
    public Func<IReadOnlyList<Rectangle>>? ObstaclesProvider { get; set; }

    /// <summary>This window's own rectangle in screen pixels; empty before it has a handle.</summary>
    public Rectangle ScreenRect
    {
        get
        {
            var rect = GetOwnWindowRect();
            return rect is null
                ? Rectangle.Empty
                : Rectangle.FromLTRB(rect.Value.Left, rect.Value.Top, rect.Value.Right, rect.Value.Bottom);
        }
    }

    /// <summary>
    /// True while the user has asked for this popup to stay put. The app checks it before
    /// superseding the popup with a new capture.
    /// </summary>
    public bool IsPinned => _pinned;

    public ResultWindow(Bitmap image, Rectangle selection, PopupTheme theme)
    {
        InitializeComponent();
        ApplyTheme(theme);
        Image = image;
        _selection = selection;
        _lifetimeToken = _lifetime.Token;

        // Content changes (recognizing -> result) resize the window, and it has to stay
        // anchored to the selection when that happens.
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>
    /// Swaps in the chosen scheme's colours. The XAML asks for these by DynamicResource,
    /// so replacing the entries here repaints everything, including the pieces inside
    /// control templates.
    /// </summary>
    private void ApplyTheme(PopupTheme theme)
    {
        // System.Drawing is also in scope here, so the media Color has to be spelled out.
        void Set(string key, System.Windows.Media.Color color) =>
            Resources[key] = new SolidColorBrush(color);

        Set("WindowBg", theme.Window);
        Set("Surface", theme.Surface);
        Set("Ink", theme.Ink);
        Set("Muted", theme.Muted);
        Set("Accent", theme.Accent);
        Set("Warn", theme.Warn);
        Set("Line", theme.Line);
        Set("ButtonBg", theme.ButtonBackground);
        Set("ButtonBorder", theme.ButtonBorder);
        Set("ButtonHover", theme.ButtonHover);
        Set("PrimaryBg", theme.PrimaryBackground);
        Set("PrimaryInk", theme.PrimaryInk);
        Set("Selection", theme.Selection);
    }

    // ------------------------------------------------------------------ show

    public void ShowNoActivate()
    {
        Show();
        Reposition();
        _watcher = new DismissWatcher(GetOwnWindowRect, DismissFromWatcher);
        UpdatePinAppearance();
    }

    /// <summary>
    /// Takes a pinned popup off the screen for the duration of a capture. It is topmost, so
    /// leaving it up would bake it into the frozen screenshot and float it over the dimming
    /// mask - the same defect that made the very first popup look like a dead hotkey.
    /// Uses SW_HIDE/SW_SHOWNA rather than WPF's Hide()/Show() so re-showing cannot activate
    /// the window, which is the one thing this popup must never do.
    /// </summary>
    public void HideForCapture() => ShowNative(SW_HIDE);

    public void ShowAfterCapture() => ShowNative(SW_SHOWNA);

    private void ShowNative(int command)
    {
        if (_closing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, command);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // Windows 11 rounds the corners for us. On Windows 10 this call fails harmlessly
        // and the window is simply square - a fair trade for keeping subpixel text.
        var preference = DWMWCP_ROUND;
        try
        {
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn($"设置窗口圆角失败（Windows 10 上属正常）：{ex.Message}");
        }
    }

    /// <summary>
    /// Re-anchors the popup beside the selection. Deferred to a Loaded-priority dispatcher
    /// callback: SizeChanged fires while SizeToContent is still settling, and measuring the
    /// window then yields an intermediate size, which is how the popup ended up hanging off
    /// the screen edge. By the time this runs the HWND has its final size.
    /// </summary>
    private void Reposition()
    {
        // Once the user has dragged it somewhere, that is where it belongs.
        if (_userMoved) return;

        // Streaming text raises SizeChanged on nearly every chunk. Without this guard each
        // one would queue its own callback and the popup would be measured and moved dozens
        // of times per translation; one pending callback does the same job.
        if (_repositionQueued) return;
        _repositionQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _repositionQueued = false;
            if (_userMoved || _closing) return;

            var rect = GetOwnWindowRect();
            if (rect is null) return;

            var size = new Size(rect.Value.Right - rect.Value.Left, rect.Value.Bottom - rect.Value.Top);
            if (size.Width <= 0 || size.Height <= 0) return;

            MoveTo(PopupPlacement.Compute(_selection, size, ObstaclesProvider?.Invoke()));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MoveTo(Point point)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetWindowPos(hwnd, HWND_TOPMOST, point.X, point.Y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private DismissWatcher.RECT? GetOwnWindowRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowRect(hwnd, out var r) ? r : null;
    }

    // ----------------------------------------------------------------- states

    public void SetRecognizing()
    {
        HideAllButtons();
        StatusText.Text = "正在识别文字…";
        BodyText.Text = "…";
        BodyText.Foreground = (Brush)FindResource("Muted");
        OriginalBlock.Visibility = Visibility.Collapsed;
    }

    /// <summary>Reports an OCR outcome that will not proceed to translation.</summary>
    public void SetOcrResult(OcrOutcome outcome)
    {
        HideAllButtons();
        _settled = true;
        _missingLanguages = outcome.MissingLanguages;
        _originalText = outcome.Text;
        _translationText = "";
        OriginalBlock.Visibility = Visibility.Collapsed;

        switch (outcome.Status)
        {
            case OcrStatus.Success:
                // Translation takes over from here; show the text meanwhile.
                StatusText.Text = $"{OcrLanguages.DisplayFor(outcome.LanguageTag)}　·　{outcome.Text.Length} 字";
                BodyText.Text = outcome.Text;
                BodyText.Foreground = (Brush)FindResource("Ink");
                CopyOriginalButton.Visibility = Visibility.Visible;
                break;

            case OcrStatus.NoText:
                StatusText.Text = "没识别到文字";
                BodyText.Text = "这块区域里没有找到文字。\n框得更贴近文字、或者框大一点再试试。";
                BodyText.Foreground = (Brush)FindResource("Muted");
                break;

            case OcrStatus.MissingLanguagePack:
                var names = string.Join("、", outcome.MissingLanguages.Select(l => l.DisplayName));
                StatusText.Text = "缺少语言包";
                BodyText.Text =
                    $"Windows 上还没装{names}的文字识别语言包，所以认不出来。\n\n"
                    + "点下面的按钮安装（需要管理员权限），装完直接再框一次就行，不用重启本程序。";
                BodyText.Foreground = (Brush)FindResource("Warn");
                InstallButton.Visibility = outcome.MissingLanguages.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SettingsButton.Visibility = Visibility.Visible;
                break;

            case OcrStatus.Failed:
            default:
                StatusText.Text = "识别失败";
                BodyText.Text = outcome.Error ?? "未知错误，详情见日志。";
                BodyText.Foreground = (Brush)FindResource("Warn");
                RetryButton.Visibility = Visibility.Visible;
                break;
        }

        UpdateActionPanel();
    }

    /// <summary>OCR succeeded and the text is on its way to the translator.</summary>
    public void SetTranslating(OcrOutcome ocr)
    {
        HideAllButtons();
        _originalText = ocr.Text;
        _translationText = "";
        _streaming = false;
        _settled = false;

        _sourceLabel = OcrLanguages.DisplayFor(ocr.LanguageTag);
        StatusText.Text = $"{_sourceLabel} → 中文　·　翻译中…";
        BodyText.Text = "…";
        BodyText.Foreground = (Brush)FindResource("Muted");

        OriginalText.Text = ocr.Text;
        OriginalBlock.Visibility = _originalVisible ? Visibility.Visible : Visibility.Collapsed;

        OriginalButton.Visibility = Visibility.Visible;
        CopyOriginalButton.Visibility = Visibility.Visible;
        UpdateActionPanel();
    }

    /// <summary>
    /// Shows the translation as it is being written. Takes the whole text so far rather
    /// than the newest fragment, so a dropped or duplicated call cannot corrupt what is on
    /// screen - the last one to arrive is always right.
    /// </summary>
    public void ShowPartialTranslation(string textSoFar)
    {
        // IProgress delivers asynchronously, so the last chunk of a stream can arrive
        // after the finished result has already been put on screen. Without this the late
        // chunk would overwrite it - and with it the "may be incomplete" warning colour.
        if (_closing || _settled || string.IsNullOrEmpty(textSoFar)) return;

        if (!_streaming)
        {
            _streaming = true;
            BodyText.Foreground = (Brush)FindResource("Ink");
        }

        BodyText.Text = textSoFar;

        // Follow the newest text once the body is tall enough to scroll. Only while the
        // stream is running; after that the user is reading and gets to keep their place.
        BodyScroll.ScrollToEnd();
    }

    public void SetTranslationResult(TranslationOutcome outcome)
    {
        HideAllButtons();
        _streaming = false;
        _settled = true;
        OriginalButton.Visibility = Visibility.Visible;
        CopyOriginalButton.Visibility = Visibility.Visible;
        RetranslateButton.Visibility = Visibility.Visible;

        if (outcome.IsSuccess)
        {
            _translationText = outcome.Text;
            StatusText.Text = outcome.Truncated
                ? $"{_sourceLabel} → 中文　·　译文可能不完整"
                : $"{_sourceLabel} → 中文　·　{outcome.ElapsedMs} ms";
            BodyText.Text = outcome.Text;
            BodyText.Foreground = (Brush)FindResource(outcome.Truncated ? "Warn" : "Ink");
            CopyButton.Visibility = Visibility.Visible;
        }
        else
        {
            // A failed translation must never cost the user the recognized text — that is
            // still useful on its own, so it gets revealed automatically.
            _translationText = "";
            StatusText.Text = outcome.Status == TranslationStatus.NotConfigured ? "还没配置翻译服务" : "翻译失败";
            BodyText.Text = outcome.Message;
            BodyText.Foreground = (Brush)FindResource("Warn");
            if (outcome.Status == TranslationStatus.NotConfigured)
                RetranslateButton.Visibility = Visibility.Collapsed;
            ShowOriginal(true);
        }

        UpdateActionPanel();
    }

    private void HideAllButtons()
    {
        // A button that still reads "已复制" from a moment ago would come back wearing
        // that label the next time it is shown.
        RestoreCopyLabel();

        CopyButton.Visibility = Visibility.Collapsed;
        CopyOriginalButton.Visibility = Visibility.Collapsed;
        OriginalButton.Visibility = Visibility.Collapsed;
        RetranslateButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateActionPanel()
    {
        var any = CopyButton.Visibility == Visibility.Visible
                  || CopyOriginalButton.Visibility == Visibility.Visible
                  || OriginalButton.Visibility == Visibility.Visible
                  || RetranslateButton.Visibility == Visibility.Visible
                  || RetryButton.Visibility == Visibility.Visible
                  || InstallButton.Visibility == Visibility.Visible
                  || SettingsButton.Visibility == Visibility.Visible;
        ActionPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOriginal(bool visible)
    {
        _originalVisible = visible;
        OriginalBlock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OriginalButton.Content = visible ? "隐藏原文" : "显示原文";
    }

    // --------------------------------------------------------------- dragging

    /// <summary>
    /// Manual drag rather than Window.DragMove(): DragMove goes through the non-client
    /// caption path, which expects an activatable window, and this one deliberately is not.
    /// The same applies to resizing, so the bottom-right corner is handled here too rather
    /// than by a real sizing frame. Working in raw cursor pixels keeps the popup in the
    /// same coordinate space as everything else here.
    ///
    /// Buttons mark MouseLeftButtonDown handled, so clicking one never starts either.
    /// </summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;
        var rect = GetOwnWindowRect();
        if (rect is null) return;

        var origin = new Point(cursor.X, cursor.Y);

        switch (HitTest(origin, rect.Value))
        {
            case Handle.Resize:
                _resizing = true;
                _resizeCursorOrigin = origin;
                _resizeWindowOrigin = new Size(
                    rect.Value.Right - rect.Value.Left, rect.Value.Bottom - rect.Value.Top);
                EnterManualSize();
                break;

            case Handle.Move:
                _dragging = true;
                _dragCursorOrigin = origin;
                _dragWindowOrigin = new Point(rect.Value.Left, rect.Value.Top);
                break;

            default:
                return;   // the middle is content, not a handle
        }

        RootBorder.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;
        var here = new Point(cursor.X, cursor.Y);

        if (_resizing) { ApplyResize(here); return; }
        if (_dragging) { ApplyDrag(here); return; }

        // Idle: the cursor promises exactly what a press here would do.
        var rect = GetOwnWindowRect();
        RootBorder.Cursor = (rect is null ? Handle.None : HitTest(here, rect.Value)) switch
        {
            Handle.Resize => Cursors.SizeNWSE,
            Handle.Move => Cursors.SizeAll,
            _ => Cursors.Arrow,
        };
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging && !_resizing) return;
        _dragging = false;
        _resizing = false;
        RootBorder.ReleaseMouseCapture();
    }

    private enum Handle
    {
        /// <summary>Content. A press here does nothing to the window.</summary>
        None,
        Move,
        Resize,
    }

    /// <summary>
    /// Decides what the cursor is over, by distance from the window's edges rather than by
    /// which element was hit. Elements small enough to look right are small enough to miss
    /// by a pixel, and a missed grip used to move the window instead of resizing it.
    ///
    /// The frame is the handle and the middle is content, which is what lets the body text
    /// be selected with the mouse without fighting a window drag.
    /// </summary>
    private Handle HitTest(Point cursor, DismissWatcher.RECT rect)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int Wide(int dip) => (int)Math.Round(dip * dpi.DpiScaleX);
        int Tall(int dip) => (int)Math.Round(dip * dpi.DpiScaleY);

        if (cursor.X >= rect.Right - Wide(ResizeCornerDip)
            && cursor.Y >= rect.Bottom - Tall(ResizeCornerDip))
        {
            return Handle.Resize;
        }

        var onFrame = cursor.X < rect.Left + Wide(DragBorderDip)
                      || cursor.X >= rect.Right - Wide(DragBorderDip)
                      || cursor.Y < rect.Top + Tall(DragBorderDip)
                      || cursor.Y >= rect.Bottom - Tall(DragBorderDip);

        return onFrame ? Handle.Move : Handle.None;
    }

    private void ApplyDrag(Point cursor)
    {
        var dx = cursor.X - _dragCursorOrigin.X;
        var dy = cursor.Y - _dragCursorOrigin.Y;
        if (dx == 0 && dy == 0) return;

        // Only an actual movement counts as "the user placed this window", and only then
        // does auto-positioning stop. Setting it on mouse-down instead meant a mere click
        // froze the position, so a popup that later grew with the translation stayed put
        // and hung off the screen edge.
        _userMoved = true;

        MoveTo(new Point(_dragWindowOrigin.X + dx, _dragWindowOrigin.Y + dy));
    }

    private void ApplyResize(Point cursor)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var minWidth = (int)Math.Round(240 * dpi.DpiScaleX);
        var minHeight = (int)Math.Round(110 * dpi.DpiScaleY);

        var width = Math.Max(minWidth, _resizeWindowOrigin.Width + (cursor.X - _resizeCursorOrigin.X));
        var height = Math.Max(minHeight, _resizeWindowOrigin.Height + (cursor.Y - _resizeCursorOrigin.Y));

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // NOMOVE: the top-left stays put, which is what dragging a bottom-right corner means.
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, width, height,
            SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Hands the window's size over to the user. Auto-sizing and a chosen size cannot both
    /// be in effect: while SizeToContent is on, WPF recomputes the size from the content
    /// and undoes every drag. The caps that shape the auto-sized popup come off at the same
    /// time, and the body row switches to filling whatever height is left - otherwise the
    /// text keeps its own height and a taller window is just empty space.
    /// </summary>
    private void EnterManualSize()
    {
        if (_manualSize) return;
        _manualSize = true;

        SizeToContent = SizeToContent.Manual;
        ContentGrid.MinWidth = 0;
        ContentGrid.MaxWidth = double.PositiveInfinity;
        BodyScroll.MaxHeight = double.PositiveInfinity;
        BodyRow.Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);

        // Sizing it by hand is as much a placement decision as moving it, so stop
        // re-anchoring it to the selection from here on.
        _userMoved = true;
    }

    // ---------------------------------------------------------------- actions

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Copies the selected part when there is a selection, the whole thing otherwise. Each
    /// button owns one of the two text boxes, so "which selection does this copy" is never
    /// ambiguous - and the label says which it is about to do.
    /// </summary>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = BodyText.SelectedText;
        FlashCopy(CopyButton, TrySetClipboard(selected.Length > 0 ? selected : _translationText));
    }

    private void CopyOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = OriginalText.SelectedText;
        FlashCopy(CopyOriginalButton, TrySetClipboard(selected.Length > 0 ? selected : _originalText));
    }

    private void AnyText_SelectionChanged(object sender, RoutedEventArgs e) => UpdateCopyLabels();

    private void UpdateCopyLabels()
    {
        // Can fire from the template before the named fields are assigned.
        if (CopyButton is null || CopyOriginalButton is null) return;

        // A "已复制" flash is on screen; it puts the right label back when it expires.
        if (_restoreCopyLabel is not null) return;

        CopyButton.Content = BodyText.SelectionLength > 0 ? "复制选中" : "复制译文";
        CopyOriginalButton.Content = OriginalText.SelectionLength > 0 ? "复制选中" : "复制原文";
    }

    /// <summary>
    /// Says what happened on the button itself, then puts the label back. Previously the
    /// button just kept saying "已复制" forever, which made the second copy look like it
    /// had not registered.
    /// </summary>
    private void FlashCopy(Button button, bool ok)
    {
        RestoreCopyLabel();

        button.Content = ok ? "已复制" : "复制失败";
        _restoreCopyLabel = UpdateCopyLabels;

        _copyFeedback = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1400),
        };
        _copyFeedback.Tick += (_, _) => RestoreCopyLabel();
        _copyFeedback.Start();
    }

    private void RestoreCopyLabel()
    {
        _copyFeedback?.Stop();
        _copyFeedback = null;

        var restore = _restoreCopyLabel;
        _restoreCopyLabel = null;
        if (restore is not null && !_closing) restore();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => LanguagePackHelper.OpenLanguageSettings();

    private void OriginalButton_Click(object sender, RoutedEventArgs e) => ShowOriginal(!_originalVisible);

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void SetPinned(bool value)
    {
        _pinned = value;

        // Disposed rather than merely suspended: a pinned popup can sit on screen for a
        // long time, and a suspended watcher would still be waking up 25 times a second to
        // decide it has nothing to do. Unpinning builds a fresh one, which also re-seeds
        // the mouse state so the click that unpinned it does not immediately close it.
        if (value)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        else if (_watcher is null && !_closing)
        {
            _watcher = new DismissWatcher(GetOwnWindowRect, DismissFromWatcher);
        }

        UpdatePinAppearance();
    }

    private void UpdatePinAppearance()
    {
        PinButton.Content = _pinned ? "已钉住" : "钉住";
        PinButton.Foreground = (Brush)FindResource(_pinned ? "Accent" : "Muted");
        PinButton.ToolTip = _pinned
            ? "已钉住：按 Esc 或点别处都不会关它，再框选一次也会留着。再点一下取消。"
            : "钉住：让这个小窗留在屏幕上，点别处也不关。";
    }

    private async void RetranslateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RetranslateHandler is null) return;

        RetranslateButton.IsEnabled = false;
        try
        {
            await RetranslateHandler();
        }
        catch (Exception ex)
        {
            Log.Error("重新翻译失败", ex);
        }
        finally
        {
            RetranslateButton.IsEnabled = true;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (RetryHandler is null) return;

        RetryButton.IsEnabled = false;
        try
        {
            await RetryHandler();
        }
        catch (Exception ex)
        {
            Log.Error("重试失败", ex);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_missingLanguages.Count == 0) return;

        // The install puts an elevated console on screen for several seconds. Clicking it
        // would otherwise register as "clicked outside" and close this popup while the
        // handler below is still writing to it.
        _watcher?.Suspend();

        InstallButton.IsEnabled = false;
        var original = InstallButton.Content;
        InstallButton.Content = "安装中…";
        StatusText.Text = "正在安装语言包，请在弹出的窗口里允许管理员权限";

        try
        {
            var allInstalled = true;
            foreach (var language in _missingLanguages)
            {
                var (installed, message) = await LanguagePackHelper.TryInstallAsync(language);
                if (!installed)
                {
                    allInstalled = false;
                    BodyText.Text = message;
                    break;
                }
            }

            if (_closing) return;   // closed while the installer was running

            if (allInstalled)
            {
                StatusText.Text = "语言包已装好，正在重新识别…";
                if (RetryHandler is not null) await RetryHandler();
            }
        }
        catch (Exception ex)
        {
            Log.Error("安装语言包时出错", ex);
            if (!_closing) BodyText.Text = $"安装出错：{ex.Message}";
        }
        finally
        {
            if (!_closing)
            {
                InstallButton.IsEnabled = true;
                InstallButton.Content = original;
            }
            // A popup pinned during the install has no watcher to resume, and must not
            // gain one here.
            if (!_pinned) _watcher?.Resume();
        }
    }

    private static bool TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // The clipboard is a shared, single-owner resource; another app holding it open
        // makes this throw, and retrying a moment later almost always succeeds.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"写剪贴板失败（第 {attempt + 1} 次）：{ex.Message}");
                System.Threading.Thread.Sleep(60);
            }
        }
        return false;
    }

    // --------------------------------------------------------------- lifetime

    private void DismissFromWatcher()
    {
        if (_closing) return;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;

        // Cancel, but deliberately do NOT dispose: work started before the close may still
        // be registering continuations on this token, and registering against a disposed
        // source throws. The source holds no unmanaged resources, so letting the GC take it
        // costs nothing.
        try { _lifetime.Cancel(); } catch (Exception ex) { Log.Warn($"取消小窗任务时出错：{ex.Message}"); }

        _copyFeedback?.Stop();
        _copyFeedback = null;
        _watcher?.Dispose();
        _watcher = null;
        Image.Dispose();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ interop

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out DismissWatcher.RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
