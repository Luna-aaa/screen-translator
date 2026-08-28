using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Tray;

/// <summary>
/// The tray icon and its menu. This is the app's only permanent UI — there is no
/// main window, so the tray is also where errors surface as balloon notifications.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const string IconResourceName = "ScreenTranslator.Resources.app.ico";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private Icon? _ownedIcon;
    private bool _suppressAutoStartEvent;

    public event Action? CaptureRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? ExitRequested;

    public TrayIconHost()
    {
        _captureItem = new ToolStripMenuItem("框选翻译", null, (_, _) => CaptureRequested?.Invoke())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };

        _autoStartItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        _autoStartItem.CheckedChanged += (_, _) =>
        {
            if (_suppressAutoStartEvent) return;
            AutoStartToggled?.Invoke(_autoStartItem.Checked);
        };

        // ShowCheckMargin must be on: WinForms draws a menu item's check mark in the
        // image/check margin, so with both margins off a checked item looks identical
        // to an unchecked one. We keep ShowImageMargin off (no menu item has an icon)
        // and turn on the narrower check-only margin instead.
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(_captureItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "屏幕划取翻译",
            ContextMenuStrip = menu,
            Visible = false,
        };

        // Left click starts a capture; double click would race with it, so only
        // handle the single left click here.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) CaptureRequested?.Invoke();
        };
    }

    public void Show() => _notifyIcon.Visible = true;

    public void SetHotkeyHint(string? hotkeyText)
    {
        _captureItem.Text = string.IsNullOrWhiteSpace(hotkeyText) ? "框选翻译" : $"框选翻译    {hotkeyText}";

        var tip = string.IsNullOrWhiteSpace(hotkeyText)
            ? "屏幕划取翻译（快捷键未注册）"
            : $"屏幕划取翻译（{hotkeyText}）";
        // NotifyIcon.Text is capped at 63 chars by the shell.
        _notifyIcon.Text = tip.Length > 62 ? tip[..62] : tip;
    }

    public void SetAutoStartChecked(bool value)
    {
        _suppressAutoStartEvent = true;
        _autoStartItem.Checked = value;
        _suppressAutoStartEvent = false;
    }

    public void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Info, int timeoutMs = 4000)
    {
        try
        {
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(timeoutMs);
        }
        catch (Exception ex)
        {
            Log.Error("显示托盘通知失败", ex);
        }
    }

    private Icon LoadIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName);
            if (stream is not null)
            {
                // Ask for the shell's small-icon size so the tray gets the crisp
                // 16px frame instead of a downscaled 256px one.
                _ownedIcon = new Icon(stream, SystemInformation.SmallIconSize);
                return _ownedIcon;
            }
            Log.Warn($"未找到内嵌图标资源 {IconResourceName}，改用系统默认图标");
        }
        catch (Exception ex)
        {
            Log.Error("加载托盘图标失败，改用系统默认图标", ex);
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        // Explicitly hide first: a NotifyIcon that is merely disposed can leave a
        // ghost icon in the tray until the user hovers over it.
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}
