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
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly Rectangle _selection;
    private DismissWatcher? _watcher;
    private IReadOnlyList<OcrLanguage> _missingLanguages = Array.Empty<OcrLanguage>();
    private string _copyableText = "";
    private string _originalText = "";
    private string _sourceLabel = "";
    private bool _originalVisible;
    private bool _closing;

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

    /// <summary>The captured crop. Owned by this window and disposed with it.</summary>
    public Bitmap Image { get; }

    /// <summary>Set by the app; re-runs recognition on <see cref="Image"/>.</summary>
    public Func<Task>? RetryHandler { get; set; }

    public ResultWindow(Bitmap image, Rectangle selection)
    {
        InitializeComponent();
        Image = image;
        _selection = selection;
        _lifetimeToken = _lifetime.Token;

        // Content changes (recognizing -> result) resize the window, and it has to stay
        // anchored to the selection when that happens.
        SizeChanged += (_, _) => Reposition();
    }

    // ------------------------------------------------------------------ show

    public void ShowNoActivate()
    {
        Show();
        Reposition();
        _watcher = new DismissWatcher(GetOwnWindowRect, DismissFromWatcher);
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

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_userMoved || _closing) return;

            var rect = GetOwnWindowRect();
            if (rect is null) return;

            var size = new Size(rect.Value.Right - rect.Value.Left, rect.Value.Bottom - rect.Value.Top);
            if (size.Width <= 0 || size.Height <= 0) return;

            MoveTo(PopupPlacement.Compute(_selection, size));
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
        _missingLanguages = outcome.MissingLanguages;
        _copyableText = outcome.Text;
        OriginalBlock.Visibility = Visibility.Collapsed;

        switch (outcome.Status)
        {
            case OcrStatus.Success:
                // Translation takes over from here; show the text meanwhile.
                StatusText.Text = $"{OcrLanguages.DisplayFor(outcome.LanguageTag)}　·　{outcome.Text.Length} 字";
                BodyText.Text = outcome.Text;
                BodyText.Foreground = (Brush)FindResource("Ink");
                CopyButton.Content = "复制原文";
                CopyButton.Visibility = Visibility.Visible;
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
        _copyableText = ocr.Text;

        _sourceLabel = OcrLanguages.DisplayFor(ocr.LanguageTag);
        StatusText.Text = $"{_sourceLabel} → 中文　·　翻译中…";
        BodyText.Text = "…";
        BodyText.Foreground = (Brush)FindResource("Muted");

        OriginalText.Text = ocr.Text;
        OriginalBlock.Visibility = _originalVisible ? Visibility.Visible : Visibility.Collapsed;

        OriginalButton.Visibility = Visibility.Visible;
        UpdateActionPanel();
    }

    public void SetTranslationResult(TranslationOutcome outcome)
    {
        HideAllButtons();
        OriginalButton.Visibility = Visibility.Visible;

        if (outcome.IsSuccess)
        {
            _copyableText = outcome.Text;
            StatusText.Text = $"{_sourceLabel} → 中文　·　{outcome.ElapsedMs} ms";
            BodyText.Text = outcome.Text;
            BodyText.Foreground = (Brush)FindResource("Ink");
            CopyButton.Content = "复制译文";
            CopyButton.Visibility = Visibility.Visible;
        }
        else
        {
            // A failed translation must never cost the user the recognized text — that is
            // still useful on its own, so it gets revealed automatically.
            _copyableText = _originalText;
            StatusText.Text = outcome.Status == TranslationStatus.NotConfigured ? "还没配置翻译服务" : "翻译失败";
            BodyText.Text = outcome.Message;
            BodyText.Foreground = (Brush)FindResource("Warn");
            CopyButton.Content = "复制原文";
            CopyButton.Visibility = Visibility.Visible;
            RetryButton.Visibility = outcome.Status == TranslationStatus.NotConfigured
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShowOriginal(true);
        }

        UpdateActionPanel();
    }

    private void HideAllButtons()
    {
        CopyButton.Visibility = Visibility.Collapsed;
        OriginalButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateActionPanel()
    {
        var any = CopyButton.Visibility == Visibility.Visible
                  || OriginalButton.Visibility == Visibility.Visible
                  || RetryButton.Visibility == Visibility.Visible
                  || InstallButton.Visibility == Visibility.Visible
                  || SettingsButton.Visibility == Visibility.Visible;
        ActionPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOriginal(bool visible)
    {
        _originalVisible = visible;
        OriginalBlock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OriginalButton.Content = visible ? "隐藏原文" : "原文";
    }

    // --------------------------------------------------------------- dragging

    /// <summary>
    /// Manual drag rather than Window.DragMove(): DragMove goes through the non-client
    /// caption path, which expects an activatable window, and this one deliberately is not.
    /// Working in raw cursor pixels also keeps the popup in the same coordinate space as
    /// everything else here.
    ///
    /// Buttons mark MouseLeftButtonDown handled, so clicking one never starts a drag.
    /// </summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;
        var rect = GetOwnWindowRect();
        if (rect is null) return;

        _dragging = true;
        _dragCursorOrigin = new Point(cursor.X, cursor.Y);
        _dragWindowOrigin = new Point(rect.Value.Left, rect.Value.Top);
        RootBorder.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!GetCursorPos(out var cursor)) return;

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

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        RootBorder.ReleaseMouseCapture();
    }

    // ---------------------------------------------------------------- actions

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySetClipboard(_copyableText))
        {
            CopyButton.Content = "已复制";
        }
        else
        {
            CopyButton.Content = "复制失败";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => LanguagePackHelper.OpenLanguageSettings();

    private void OriginalButton_Click(object sender, RoutedEventArgs e) => ShowOriginal(!_originalVisible);

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
            _watcher?.Resume();
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
