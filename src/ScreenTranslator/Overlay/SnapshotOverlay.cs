using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ScreenTranslator.Capture;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Overlay;

/// <summary>
/// The full-virtual-desktop window that shows the translated screen.
///
/// It obeys the same coordinate contract as <see cref="SelectionOverlay"/> — client (x,y)
/// equals frozen-bitmap (x,y) equals screen (virtualRect origin + x,y) — and for the same
/// reason: the boxes OCR reported are in bitmap pixels, and anything that rescaled them
/// on the way to the screen would put every translation slightly off its own words.
///
/// It is up while the work happens, not after. Recognition plus a round trip for forty
/// blocks takes several seconds, and a frozen desktop with no explanation reads as a
/// crash; the status bar is the difference between "it is working" and "it broke".
/// </summary>
internal sealed class SnapshotOverlay : Form
{
    private static readonly Color BarBack = Color.FromArgb(238, 20, 22, 30);
    private static readonly Color BarInk = Color.FromArgb(240, 244, 248);
    private static readonly Color BarMuted = Color.FromArgb(168, 176, 194);
    private static readonly Color Accent = Color.FromArgb(88, 132, 255);

    private static readonly Color ButtonBack = Color.FromArgb(46, 50, 64);
    private static readonly Color ButtonHot = Color.FromArgb(70, 88, 140);
    private static readonly Color SelectFill = Color.FromArgb(64, 88, 132, 255);
    private static readonly Color SelectEdge = Color.FromArgb(220, 120, 170, 255);

    private const int BarPadX = 18;
    private const int BarPadY = 11;
    private const int BarMargin = 26;
    private const int ButtonPadX = 14;
    private const int ButtonPadY = 6;
    private const int ButtonGap = 8;

    /// <summary>Movement below this is a click, not a drag. Keeps a twitchy click from selecting.</summary>
    private const int DragThreshold = 5;

    /// <summary>Generous enough to cover the anti-aliased edges of everything the bar draws.</summary>
    private const int BarInvalidateHeight = 200;

    private readonly Bitmap _frozen;              // owned by the caller
    private readonly Rectangle _virtualRect;
    private readonly Font _barFont;
    private readonly Font _hintFont;
    private readonly Font _buttonFont;

    private Bitmap? _translated;                  // owned by this form
    private IReadOnlyList<RenderBlock> _blocks = Array.Empty<RenderBlock>();
    private string _saveDirectory = "";

    private bool _showingOriginal;
    private string _status = "正在准备…";
    private string _hint = "Esc 取消";
    private bool _busy = true;

    private readonly List<OverlayButton> _buttons = new();
    private OverlayButton? _hotButton;

    private Point _dragStart;
    private bool _mouseDown;
    private bool _dragging;
    private Rectangle _dragRect;

    /// <summary>Blocks the last drag picked out, highlighted until the next action.</summary>
    private readonly List<RenderBlock> _selected = new();

    private readonly TaskCompletionSource<bool> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Cancelled when the user dismisses the overlay, so in-flight work stops with it.</summary>
    public CancellationToken Lifetime { get; }

    private sealed class OverlayButton
    {
        public required string Text { get; init; }
        public required Action Action { get; init; }
        public Rectangle Bounds { get; set; }
    }

    public SnapshotOverlay(Bitmap frozen, Rectangle virtualRect)
    {
        _frozen = frozen;
        _virtualRect = virtualRect;
        Lifetime = _lifetime.Token;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Cursor = Cursors.Arrow;
        KeyPreview = true;
        Text = "ScreenTranslator.Snapshot";

        _barFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
        _hintFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        _buttonFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.X = _virtualRect.Left;
            cp.Y = _virtualRect.Top;
            cp.Width = _virtualRect.Width;
            cp.Height = _virtualRect.Height;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public Task ShowAndWaitAsync()
    {
        Show();
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
            _virtualRect.Left, _virtualRect.Top, _virtualRect.Width, _virtualRect.Height,
            NativeMethods.SWP_SHOWWINDOW);

        // Same reason as the selection mask: without foreground ownership no WM_KEYDOWN
        // arrives and Esc does nothing, while the mouse keeps working and hides the fault.
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        NativeMethods.SetFocus(Handle);

        return _closed.Task;
    }

    // ------------------------------------------------------------------ state

    public void SetStatus(string status)
    {
        _status = status;
        InvalidateBar();
    }

    /// <summary>Hands over the finished picture and the blocks behind it. The overlay owns the bitmap.</summary>
    public void SetResult(Bitmap translated, IReadOnlyList<RenderBlock> blocks, string saveDirectory)
    {
        _translated?.Dispose();
        _translated = translated;
        _blocks = blocks;
        _saveDirectory = saveDirectory;
        _busy = false;
        _showingOriginal = false;
        _hint = "拖一个框复制那几段　·　左键点一下看原图　·　空格按住对照";

        _buttons.Clear();
        _buttons.Add(new OverlayButton { Text = "复制全部", Action = () => CopyAll() });
        _buttons.Add(new OverlayButton { Text = "保存图片", Action = SaveImage });
        _buttons.Add(new OverlayButton { Text = "关闭", Action = Finish });

        Invalidate();
    }

    /// <summary>Reports a failure without closing: the frozen screen stays up so nothing is lost.</summary>
    public void SetFailure(string message)
    {
        _busy = false;
        _status = message;
        _hint = "Esc 或右键关闭";
        _buttons.Clear();
        _buttons.Add(new OverlayButton { Text = "关闭", Action = Finish });
        Invalidate();
    }

    // ----------------------------------------------------------------- input

    protected override void WndProc(ref Message m)
    {
        // The mask must never resize itself out from under the bitmap it is showing.
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // WPF owns the message loop, so WinForms' keyboard pre-processing never runs and
        // ProcessCmdKey is not called. WM_KEYDOWN always arrives.
        if (m.Msg == NativeMethods.WM_KEYDOWN)
        {
            var key = (int)m.WParam;
            var ctrl = (ModifierKeys & Keys.Control) == Keys.Control;

            if (key == NativeMethods.VK_ESCAPE) { Finish(); return; }

            if (key == VK_SPACE && _translated is not null)
            {
                ToggleOriginal(true);
                return;
            }

            if (ctrl && key == VK_C) { CopyAll(); return; }
            if (ctrl && key == VK_S) { SaveImage(); return; }
        }

        if (m.Msg == WM_KEYUP && (int)m.WParam == VK_SPACE && _translated is not null)
        {
            ToggleOriginal(false);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
        {
            Finish();
            return;
        }

        // A button takes the click before anything else can interpret it.
        var hit = _buttons.FirstOrDefault(b => b.Bounds.Contains(e.Location));
        if (hit is not null)
        {
            hit.Action();
            return;
        }

        _mouseDown = true;
        _dragging = false;
        _dragStart = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hot = _buttons.FirstOrDefault(b => b.Bounds.Contains(e.Location));
        if (!ReferenceEquals(hot, _hotButton))
        {
            _hotButton = hot;
            Cursor = hot is null ? Cursors.Arrow : Cursors.Hand;
            InvalidateBar();
        }

        if (!_mouseDown || _translated is null) return;

        if (!_dragging
            && Math.Abs(e.X - _dragStart.X) < DragThreshold
            && Math.Abs(e.Y - _dragStart.Y) < DragThreshold)
        {
            return;
        }

        _dragging = true;
        var previous = _dragRect;
        _dragRect = Rectangle.FromLTRB(
            Math.Min(_dragStart.X, e.X), Math.Min(_dragStart.Y, e.Y),
            Math.Max(_dragStart.X, e.X), Math.Max(_dragStart.Y, e.Y));

        // 2px of slack: the anti-aliased edge spills past the rectangle, and anything left
        // outside the invalidated region stays on screen as a smear.
        Invalidate(Grow(Rectangle.Union(previous, _dragRect), 3));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_mouseDown) return;
        _mouseDown = false;

        if (!_dragging)
        {
            // A plain click compares. It is the action people repeat; leaving is the one
            // they do once, which is why that lives on the right button.
            if (_translated is not null) ToggleOriginal(!_showingOriginal);
            return;
        }

        _dragging = false;
        SelectWithin(_dragRect);
        _dragRect = Rectangle.Empty;
        Invalidate();
    }

    private void ToggleOriginal(bool showOriginal)
    {
        if (_showingOriginal == showOriginal) return;
        _showingOriginal = showOriginal;
        Invalidate();
    }

    // ------------------------------------------------------------------ copy

    /// <summary>
    /// Picks out the blocks the drag touched and copies them.
    ///
    /// Selection is by block, not by character: what is on screen is a bitmap, so there is
    /// no text layout to run a caret through. Dragging a box over the paragraphs you want
    /// is the honest version of "select and copy" for a picture, and it answers the
    /// question people actually have — "give me those two paragraphs".
    /// </summary>
    private void SelectWithin(Rectangle region)
    {
        _selected.Clear();

        foreach (var block in _blocks)
        {
            var bounds = Rectangle.Round(block.Group.Bounds);
            if (region.IntersectsWith(bounds)) _selected.Add(block);
        }

        if (_selected.Count == 0)
        {
            SetStatus("这个框里没有可复制的段落");
            return;
        }

        var text = BuildText(_selected);
        if (TryCopy(text))
        {
            SetStatus($"已复制 {_selected.Count} 段{WhatKind}　·　{text.Length} 字");
        }
    }

    private void CopyAll()
    {
        if (_blocks.Count == 0) return;

        _selected.Clear();
        var text = BuildText(_blocks);
        if (TryCopy(text))
        {
            SetStatus($"已复制全部 {_blocks.Count} 段{WhatKind}　·　{text.Length} 字");
        }
        Invalidate();
    }

    /// <summary>
    /// What you are looking at is what gets copied. Holding space to read the original and
    /// then pressing copy has exactly one sensible meaning, and this is it.
    /// </summary>
    private string WhatKind => _showingOriginal ? "原文" : "译文";

    private string BuildText(IEnumerable<RenderBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks.OrderBy(b => b.Group.Bounds.Top).ThenBy(b => b.Group.Bounds.Left))
        {
            var text = _showingOriginal
                ? block.Group.Text
                : block.Translation ?? block.Group.Text;

            if (string.IsNullOrWhiteSpace(text)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    private bool TryCopy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ClipboardHelper.TrySetText(text)) return true;

        SetStatus("复制失败，剪贴板被别的程序占着，过一秒再试");
        return false;
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Writes the picture currently on screen — translated or original, whichever is
    /// showing — so "save" means the same thing as "copy": you get what you were looking at.
    /// </summary>
    private void SaveImage()
    {
        var picture = _showingOriginal || _translated is null ? _frozen : _translated;

        try
        {
            var dir = string.IsNullOrWhiteSpace(_saveDirectory) ? Paths.DefaultSnapshotDir : _saveDirectory;
            Directory.CreateDirectory(dir);

            var suffix = _showingOriginal ? "原图" : "译文";
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{suffix}.png");
            picture.Save(path, ImageFormat.Png);

            Log.Info($"整屏翻译图片已保存：{path}");
            SetStatus($"已保存到 {path}");
        }
        catch (Exception ex)
        {
            Log.Error("保存整屏翻译图片失败", ex);
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- closing

    private void Finish()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        _closed.TrySetResult(true);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        _closed.TrySetResult(true);
        base.OnFormClosed(e);
    }

    // --------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Every pixel is painted in OnPaint; filling first would only cause flicker.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var picture = _showingOriginal || _translated is null ? _frozen : _translated;
        g.DrawImageUnscaled(picture, 0, 0);

        g.CompositingMode = CompositingMode.SourceOver;
        DrawSelection(g);
        DrawBar(g);
    }

    private void DrawSelection(Graphics g)
    {
        using var fill = new SolidBrush(SelectFill);
        using var edge = new SolidBrush(SelectEdge);

        foreach (var block in _selected)
        {
            var r = Rectangle.Round(block.Group.Bounds);
            r.Inflate(3, 2);
            g.FillRectangle(fill, r);
            // FillRectangle, never DrawRectangle: a 1px pen under PixelOffsetMode.Half
            // lands on the neighbouring row and leaves a line outside the repainted area.
            g.FillRectangle(edge, r.X, r.Y, r.Width, 1);
            g.FillRectangle(edge, r.X, r.Bottom - 1, r.Width, 1);
            g.FillRectangle(edge, r.X, r.Y, 1, r.Height);
            g.FillRectangle(edge, r.Right - 1, r.Y, 1, r.Height);
        }

        if (_dragRect.Width <= 0 || _dragRect.Height <= 0) return;

        using var dragFill = new SolidBrush(Color.FromArgb(38, 132, 170, 255));
        g.FillRectangle(dragFill, _dragRect);
        g.FillRectangle(edge, _dragRect.X, _dragRect.Y, _dragRect.Width, 1);
        g.FillRectangle(edge, _dragRect.X, _dragRect.Bottom - 1, _dragRect.Width, 1);
        g.FillRectangle(edge, _dragRect.X, _dragRect.Y, 1, _dragRect.Height);
        g.FillRectangle(edge, _dragRect.Right - 1, _dragRect.Y, 1, _dragRect.Height);
    }

    private void DrawBar(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var status = _busy ? $"◐  {_status}" : _status;
        var statusSize = g.MeasureString(status, _barFont);
        var hintSize = g.MeasureString(_hint, _hintFont);

        // Bar geometry is measured, never hard-coded: fonts are in points, so on a
        // 150%-scaled display they render half again as large and a fixed box would clip.
        var buttonHeight = 0f;
        var buttonWidth = 0f;
        if (_buttons.Count > 0)
        {
            foreach (var button in _buttons)
            {
                var size = g.MeasureString(button.Text, _buttonFont);
                button.Bounds = new Rectangle(0, 0,
                    (int)Math.Ceiling(size.Width) + ButtonPadX * 2,
                    (int)Math.Ceiling(size.Height) + ButtonPadY * 2);
                buttonWidth += button.Bounds.Width + ButtonGap;
                buttonHeight = Math.Max(buttonHeight, button.Bounds.Height);
            }
            buttonWidth -= ButtonGap;
        }

        var contentWidth = Math.Max(Math.Max(statusSize.Width, hintSize.Width), buttonWidth);
        var width = (int)Math.Ceiling(contentWidth) + BarPadX * 2;
        var height = (int)Math.Ceiling(statusSize.Height + hintSize.Height + buttonHeight)
                     + BarPadY * 2 + (buttonHeight > 0 ? 14 : 4);

        var bar = BarRect(width, height);

        using (var back = new SolidBrush(BarBack))
        using (var path = RoundedRect(bar, 10))
        {
            g.FillPath(back, path);
        }

        using var inkBrush = new SolidBrush(_busy ? Accent : BarInk);
        g.DrawString(status, _barFont, inkBrush, bar.X + BarPadX, bar.Y + BarPadY);

        using var mutedBrush = new SolidBrush(BarMuted);
        var hintY = bar.Y + BarPadY + statusSize.Height + 2;
        g.DrawString(_hint, _hintFont, mutedBrush, bar.X + BarPadX, hintY);

        if (_buttons.Count == 0) return;

        var x = bar.X + BarPadX;
        var y = (int)(hintY + hintSize.Height + 8);
        foreach (var button in _buttons)
        {
            button.Bounds = new Rectangle(x, y, button.Bounds.Width, button.Bounds.Height);

            using var face = new SolidBrush(ReferenceEquals(button, _hotButton) ? ButtonHot : ButtonBack);
            using var shape = RoundedRect(button.Bounds, 6);
            g.FillPath(face, shape);

            var size = g.MeasureString(button.Text, _buttonFont);
            g.DrawString(button.Text, _buttonFont, inkBrush,
                button.Bounds.X + (button.Bounds.Width - size.Width) / 2,
                button.Bounds.Y + (button.Bounds.Height - size.Height) / 2);

            x += button.Bounds.Width + ButtonGap;
        }
    }

    /// <summary>
    /// Centred near the top of the monitor the mouse is on, so on a multi-monitor desktop
    /// the bar does not end up on a screen the user is not looking at.
    /// </summary>
    private Rectangle BarRect(int width, int height)
    {
        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        var localX = screen.X - _virtualRect.X;
        var localY = screen.Y - _virtualRect.Y;

        var x = localX + (screen.Width - width) / 2;
        var y = localY + BarMargin;
        return new Rectangle(x, y, width, height);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Rectangle Grow(Rectangle r, int by)
    {
        r.Inflate(by, by);
        return r;
    }

    private void InvalidateBar()
    {
        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        var localX = screen.X - _virtualRect.X;
        var localY = screen.Y - _virtualRect.Y;
        Invalidate(new Rectangle(localX, localY + BarMargin - 4, screen.Width, BarInvalidateHeight));
    }

    // ------------------------------------------------------------------ win32

    private const int WM_KEYUP = 0x0101;
    private const int VK_SPACE = 0x20;
    private const int VK_C = 0x43;
    private const int VK_S = 0x53;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _translated?.Dispose();
            _barFont.Dispose();
            _hintFont.Dispose();
            _buttonFont.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
