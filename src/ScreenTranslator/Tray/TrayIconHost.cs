using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ScreenTranslator.History;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Tray;

/// <summary>
/// The tray icon and its menu. This is the app's only permanent UI — there is no
/// main window, so the tray is also where errors surface as balloon notifications.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const string IconResourceName = "ScreenTranslator.Resources.app.ico";

    private const int MenuHistoryCount = 8;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _historyItem;
    private Icon? _ownedIcon;
    private bool _suppressAutoStartEvent;

    public event Action? CaptureRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? ExitRequested;

    /// <summary>Asked for the current list every time the submenu opens.</summary>
    public Func<IReadOnlyList<TranslationRecord>>? HistoryProvider { get; set; }

    public event Action<TranslationRecord>? HistoryEntryChosen;
    public event Action? HistoryWindowRequested;

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
        // Rebuilt each time it opens rather than kept in sync: the list changes on every
        // translation, and a menu nobody is looking at is not worth maintaining.
        _historyItem = new ToolStripMenuItem("最近的翻译");
        _historyItem.DropDownOpening += (_, _) => RebuildHistoryMenu();
        _historyItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });

        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(_captureItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_historyItem);
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

    private void RebuildHistoryMenu()
    {
        var items = _historyItem.DropDownItems;

        // Copy out, detach, THEN dispose. ToolStripItem.Dispose() removes the item from its
        // owner's collection, so disposing while enumerating that same collection throws
        // "Collection was modified" - which surfaced as a .NET error dialog the moment the
        // submenu was opened with any history in it.
        var previous = new ToolStripItem[items.Count];
        items.CopyTo(previous, 0);
        items.Clear();
        foreach (var old in previous) old.Dispose();

        var records = HistoryProvider?.Invoke() ?? Array.Empty<TranslationRecord>();

        if (records.Count == 0)
        {
            items.Add(new ToolStripMenuItem("（还没有记录）") { Enabled = false });
            return;
        }

        foreach (var record in records.Take(MenuHistoryCount))
        {
            var captured = record;
            items.Add(new ToolStripMenuItem(captured.Summary(), null, (_, _) => HistoryEntryChosen?.Invoke(captured))
            {
                // The menu item can only show one short line; the tooltip is where the
                // whole thing is actually readable.
                ToolTipText = Tooltip(captured),
            });
        }

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("全部记录…", null, (_, _) => HistoryWindowRequested?.Invoke()));
    }

    private static string Tooltip(TranslationRecord record)
    {
        // The shell truncates a tray tooltip well before this, but clipping it ourselves
        // keeps the cut at a sensible place instead of mid-character.
        static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";

        return $"{record.Time:MM-dd HH:mm}\n\n{Clip(record.Translation)}\n\n—— 原文 ——\n{Clip(record.Original)}";
    }

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
