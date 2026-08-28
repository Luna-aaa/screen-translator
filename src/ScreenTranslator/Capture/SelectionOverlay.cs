using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Capture;

/// <summary>
/// The full-virtual-desktop dimming mask the user drags a selection on.
///
/// Coordinate contract — the whole point of this class:
///   client (x, y)  ==  frozen bitmap (x, y)  ==  screen (virtualRect.X + x, virtualRect.Y + y)
/// The window is positioned by raw SetWindowPos at the virtual-desktop rectangle and
/// nothing here is ever scaled, so those three spaces stay identical no matter how many
/// monitors there are or what each one's scale factor is. Cropping is then a plain
/// sub-rectangle copy and cannot be misaligned.
/// </summary>
internal sealed class SelectionOverlay : Form
{
    private const int DimAlpha = 115;          // ~45% black
    private const int BorderThickness = 2;
    private const int MinSelection = 2;        // px; below this a drag counts as a stray click

    private const int MagSrcW = 25;            // odd, so there is an exact centre pixel
    private const int MagSrcH = 17;
    private const int MagZoom = 6;
    private const int MagImageW = MagSrcW * MagZoom;
    private const int MagImageH = MagSrcH * MagZoom;
    private const int MagTextH = 26;
    private const int MagW = MagImageW;
    private const int MagH = MagImageH + MagTextH;
    private const int MagGap = 22;             // distance from the cursor

    private static readonly Color Accent = Color.FromArgb(255, 88, 132, 255);
    private static readonly Color ChromeBack = Color.FromArgb(225, 22, 24, 32);

    private readonly Bitmap _frozen;           // owned by the caller
    private readonly Bitmap _dimmed;           // owned by this form
    private readonly Rectangle _virtualRect;
    private readonly Rectangle _canvas;        // client-space bounds, i.e. (0,0,w,h)

    private readonly Font _badgeFont;
    private readonly Font _magFont;
    private readonly Font _hintFont;

    private readonly TaskCompletionSource<Rectangle?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Kept for the lifetime of the form purely to measure text. Chrome boxes are sized
    /// from the measured string rather than constants: fonts are specified in points, so
    /// on a 150%-scaled display they render half again as large and any hard-coded box
    /// would clip. Measuring also guarantees the rectangle used for invalidation is the
    /// exact one that gets drawn, which is what keeps stale pixels off the screen.
    /// </summary>
    private Graphics? _measure;

    private Point _cursor;
    private Point _anchor;
    private Rectangle _selection;
    private bool _dragging;
    private bool _showHint = true;
    private bool _finished;
    private Rectangle? _result;

    public SelectionOverlay(Bitmap frozen, Rectangle virtualRect)
    {
        _frozen = frozen;
        _virtualRect = virtualRect;
        _canvas = new Rectangle(0, 0, virtualRect.Width, virtualRect.Height);
        _dimmed = ScreenGrabber.BuildDimmed(frozen, DimAlpha);

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Text = "ScreenTranslator.Overlay";

        _badgeFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        _magFont = new Font("Consolas", 9f, FontStyle.Regular);
        _hintFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
    }

    /// <summary>
    /// Shows the window and pins it to the exact virtual-desktop rectangle. WinForms has
    /// its own opinions about window bounds in a per-monitor-DPI process; SetWindowPos
    /// has none, so it gets the last word.
    /// </summary>
    public void ShowAtExactGeometry()
    {
        Show();
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
            _virtualRect.Left, _virtualRect.Top, _virtualRect.Width, _virtualRect.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Resolves with the selection in client/bitmap coordinates, or null if cancelled.</summary>
    public Task<Rectangle?> ShowAndWaitAsync()
    {
        ShowAtExactGeometry();

        // Go through the Win32 calls directly, not just Form.Activate(). Without
        // foreground ownership the overlay never receives WM_KEYDOWN and Esc does
        // nothing, while right-click still works - mouse messages do not need focus.
        // This is allowed here because a global hotkey grants the process the right to
        // take the foreground.
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        NativeMethods.SetFocus(Handle);

        _cursor = PointToClient(Cursor.Position);
        Invalidate();

        return _completion.Task;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Give the window its final geometry at creation time so it never appears
            // at the wrong size for a frame.
            cp.X = _virtualRect.Left;
            cp.Y = _virtualRect.Top;
            cp.Width = _virtualRect.Width;
            cp.Height = _virtualRect.Height;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;   // keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _measure = CreateGraphics();
    }

    protected override void WndProc(ref Message m)
    {
        // Swallow DPI changes. This window is measured in raw physical pixels and may
        // span monitors with different scale factors; letting WinForms "helpfully"
        // rescale it would break the 1:1 mapping the whole design rests on.
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // Esc is handled here rather than through ProcessCmdKey/OnKeyDown because this
        // WinForms window lives inside a WPF application: the Dispatcher pumps messages
        // straight to WndProc and never runs WinForms' keyboard pre-processing, so
        // ProcessCmdKey is simply never called. WM_KEYDOWN always arrives.
        if (m.Msg == NativeMethods.WM_KEYDOWN && (int)m.WParam == NativeMethods.VK_ESCAPE)
        {
            Finish(null);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            // First right-click throws away an in-progress selection; a second one
            // (with nothing selected) closes the overlay.
            if (_dragging || !_selection.IsEmpty)
            {
                var old = _selection;
                _dragging = false;
                _selection = Rectangle.Empty;
                Capture = false;
                InvalidateChrome(old, Rectangle.Empty);
            }
            else
            {
                Finish(null);
            }
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        var previousSelection = _selection;
        var hintWasVisible = _showHint;

        _dragging = true;
        _showHint = false;
        _anchor = Clamp(e.Location);
        _cursor = _anchor;
        _selection = Rectangle.Empty;
        Capture = true;

        if (hintWasVisible) Invalidate(HintBounds());
        InvalidateCrosshair(_cursor);          // erase the pre-drag crosshair
        InvalidateChrome(previousSelection, _selection);
        InvalidateMagnifier(_cursor, _cursor);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var previousCursor = _cursor;
        var previousSelection = _selection;

        _cursor = Clamp(e.Location);
        if (_cursor == previousCursor) return;

        if (_dragging) _selection = FromPoints(_anchor, _cursor);

        if (_dragging)
        {
            InvalidateChrome(previousSelection, _selection);
        }
        else
        {
            InvalidateCrosshair(previousCursor);
            InvalidateCrosshair(_cursor);
        }

        InvalidateMagnifier(previousCursor, _cursor);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_dragging) return;

        _dragging = false;
        Capture = false;

        var selection = _selection;
        if (selection.Width < MinSelection || selection.Height < MinSelection)
        {
            // A click rather than a drag. Stay open so a stray click costs nothing.
            _selection = Rectangle.Empty;
            InvalidateChrome(selection, Rectangle.Empty);
            InvalidateCrosshair(_cursor);
            return;
        }

        Finish(selection);
    }

    private void Finish(Rectangle? selection)
    {
        if (_finished) return;
        _finished = true;
        _result = selection;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        // Guarantees the awaiting task always completes, including on an external close.
        _completion.TrySetResult(_result);
    }

    // ----------------------------------------------------------------- drawing

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Every pixel is painted in OnPaint; filling first would only cause flicker.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = Rectangle.Intersect(e.ClipRectangle, _canvas);
        if (clip.Width <= 0 || clip.Height <= 0) return;

        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);

        // The selection shows the untouched screenshot, so what the user frames is
        // literally the pixels that get cropped.
        if (!_selection.IsEmpty)
        {
            var bright = Rectangle.Intersect(_selection, clip);
            if (bright.Width > 0 && bright.Height > 0)
                g.DrawImage(_frozen, bright, bright, GraphicsUnit.Pixel);
        }

        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        if (!_selection.IsEmpty)
        {
            DrawSelectionBorder(g);
            DrawSizeBadge(g);
        }
        else if (!_dragging)
        {
            DrawCrosshair(g);
        }

        if (_showHint) DrawHint(g);

        DrawMagnifier(g);
    }

    private void DrawSelectionBorder(Graphics g)
    {
        using var brush = new SolidBrush(Accent);
        var s = _selection;
        const int t = BorderThickness;
        // Drawn entirely outside the selection so it never covers a captured pixel.
        g.FillRectangle(brush, s.X - t, s.Y - t, s.Width + t * 2, t);
        g.FillRectangle(brush, s.X - t, s.Bottom, s.Width + t * 2, t);
        g.FillRectangle(brush, s.X - t, s.Y, t, s.Height);
        g.FillRectangle(brush, s.Right, s.Y, t, s.Height);
    }

    private void DrawSizeBadge(Graphics g)
    {
        var box = BadgeBounds(_selection);

        using var back = new SolidBrush(ChromeBack);
        using var path = RoundedRect(box, 4);
        g.FillPath(back, path);

        using var fore = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(BadgeText(_selection), _badgeFont, fore, box, format);
    }

    private void DrawCrosshair(Graphics g)
    {
        // FillRectangle, not DrawLine: a 1px pen is centred on the line and, combined with
        // PixelOffsetMode.Half, lands a row above where it was asked to. That put the
        // stroke outside the rectangle being invalidated and left a trail behind the
        // cursor. Filled rectangles address exact pixels and cannot drift.
        using var brush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
        g.FillRectangle(brush, 0, _cursor.Y, _canvas.Width, 1);
        g.FillRectangle(brush, _cursor.X, 0, 1, _canvas.Height);
    }

    /// <summary>Outlines a rectangle from filled edges, for the same pixel-exactness reason.</summary>
    private static void StrokeRect(Graphics g, Rectangle r, Color color, int thickness = 1)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, r.X, r.Y, r.Width, thickness);
        g.FillRectangle(brush, r.X, r.Bottom - thickness, r.Width, thickness);
        g.FillRectangle(brush, r.X, r.Y + thickness, thickness, r.Height - thickness * 2);
        g.FillRectangle(brush, r.Right - thickness, r.Y + thickness, thickness, r.Height - thickness * 2);
    }

    private void DrawMagnifier(Graphics g)
    {
        var box = MagnifierBounds(_cursor);
        var image = new Rectangle(box.X, box.Y, MagImageW, MagImageH);

        using var back = new SolidBrush(ChromeBack);
        g.FillRectangle(back, box);

        // Zoomed pixels, nearest-neighbour so each source pixel is a crisp square.
        var src = new Rectangle(_cursor.X - MagSrcW / 2, _cursor.Y - MagSrcH / 2, MagSrcW, MagSrcH);
        var visible = Rectangle.Intersect(src, _canvas);
        if (visible.Width > 0 && visible.Height > 0)
        {
            var dest = new Rectangle(
                image.X + (visible.X - src.X) * MagZoom,
                image.Y + (visible.Y - src.Y) * MagZoom,
                visible.Width * MagZoom,
                visible.Height * MagZoom);

            var previousInterpolation = g.InterpolationMode;
            var previousSmoothing = g.SmoothingMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(_frozen, dest, visible, GraphicsUnit.Pixel);
            g.InterpolationMode = previousInterpolation;
            g.SmoothingMode = previousSmoothing;
        }

        // Highlight the one pixel actually under the cursor.
        var px = image.X + (MagSrcW / 2) * MagZoom;
        var py = image.Y + (MagSrcH / 2) * MagZoom;
        StrokeRect(g, new Rectangle(px, py, MagZoom, MagZoom), Accent);

        StrokeRect(g, box, Color.FromArgb(120, 255, 255, 255));

        // Screen coordinates, not client ones — those are what the user can verify.
        var screenX = _virtualRect.X + _cursor.X;
        var screenY = _virtualRect.Y + _cursor.Y;
        var label = _dragging
            ? $"{_selection.Width}×{_selection.Height}"
            : $"{screenX}, {screenY}";

        using var text = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        var textRect = new RectangleF(box.X, image.Bottom + 4, box.Width, MagTextH - 6);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, _magFont, text, textRect, format);
    }

    private void DrawHint(Graphics g)
    {
        var box = HintBounds();
        using var back = new SolidBrush(ChromeBack);
        using var path = RoundedRect(box, 8);
        g.FillPath(back, path);

        using var fore = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(HintText, _hintFont, fore, box, format);
    }

    private const string HintText = "拖动鼠标框选想翻译的区域　·　Esc 或右键取消";

    // -------------------------------------------------------------- geometry

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, _canvas.Width - 1)),
        Math.Clamp(p.Y, 0, Math.Max(0, _canvas.Height - 1)));

    private static Rectangle FromPoints(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private Rectangle MagnifierBounds(Point cursor)
    {
        var x = cursor.X + MagGap;
        var y = cursor.Y + MagGap;
        if (x + MagW > _canvas.Width) x = cursor.X - MagGap - MagW;
        if (y + MagH > _canvas.Height) y = cursor.Y - MagGap - MagH;
        x = Math.Clamp(x, 0, Math.Max(0, _canvas.Width - MagW));
        y = Math.Clamp(y, 0, Math.Max(0, _canvas.Height - MagH));
        return new Rectangle(x, y, MagW, MagH);
    }

    private static string BadgeText(Rectangle selection) => $"{selection.Width} × {selection.Height}";

    private Size MeasureChrome(string text, Font font, int padX, int padY, Size fallback)
    {
        if (_measure is null) return fallback;
        var size = _measure.MeasureString(text, font);
        return new Size((int)Math.Ceiling(size.Width) + padX, (int)Math.Ceiling(size.Height) + padY);
    }

    private Rectangle BadgeBounds(Rectangle selection)
    {
        var size = MeasureChrome(BadgeText(selection), _badgeFont, 18, 10, new Size(180, 40));

        var x = selection.X - BorderThickness;
        var y = selection.Y - BorderThickness - size.Height - 4;
        if (y < 0) y = selection.Y + BorderThickness + 4;              // no room above: sit inside
        if (x + size.Width > _canvas.Width) x = _canvas.Width - size.Width;
        x = Math.Max(0, x);
        y = Math.Clamp(y, 0, Math.Max(0, _canvas.Height - size.Height));
        return new Rectangle(x, y, size.Width, size.Height);
    }

    private Rectangle HintBounds()
    {
        var screenPoint = new Point(_virtualRect.X + _cursor.X, _virtualRect.Y + _cursor.Y);
        var monitor = Screen.FromPoint(screenPoint).Bounds;
        var local = new Rectangle(
            monitor.X - _virtualRect.X, monitor.Y - _virtualRect.Y, monitor.Width, monitor.Height);

        var size = MeasureChrome(HintText, _hintFont, 44, 26, new Size(560, 60));
        size.Width = Math.Min(size.Width, local.Width);

        return new Rectangle(
            local.X + (local.Width - size.Width) / 2,
            local.Y + (int)(local.Height * 0.62),
            size.Width, size.Height);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------------------------------------------------------- invalidation

    /// <summary>
    /// Every invalidation is padded. Anti-aliased text and rounded corners bleed a pixel
    /// past the rectangle they are nominally drawn in, and a pixel that gets drawn but
    /// never invalidated is a pixel that stays on screen forever.
    /// </summary>
    private const int InvalidatePadding = 2;

    private static Rectangle Pad(Rectangle r)
    {
        if (r.IsEmpty) return Rectangle.Empty;
        r.Inflate(InvalidatePadding, InvalidatePadding);
        return r;
    }

    private void InvalidateCrosshair(Point p)
    {
        Invalidate(new Rectangle(0, p.Y - InvalidatePadding, _canvas.Width, 1 + InvalidatePadding * 2));
        Invalidate(new Rectangle(p.X - InvalidatePadding, 0, 1 + InvalidatePadding * 2, _canvas.Height));
    }

    private void InvalidateMagnifier(Point from, Point to)
    {
        Invalidate(Pad(MagnifierBounds(from)));
        if (to != from) Invalidate(Pad(MagnifierBounds(to)));
    }

    /// <summary>
    /// Repaints only what a change of selection can have altered. Extending a large
    /// selection moves its frame but leaves the interior alone, so the shared interior
    /// is excluded — otherwise every mouse move would re-blit the whole screen.
    /// </summary>
    private void InvalidateChrome(Rectangle oldSelection, Rectangle newSelection)
    {
        if (oldSelection == newSelection) return;

        using var dirty = new Region(Rectangle.Empty);
        dirty.Union(Inflate(oldSelection));
        dirty.Union(Inflate(newSelection));

        if (!oldSelection.IsEmpty && !newSelection.IsEmpty)
        {
            var shared = Rectangle.Intersect(oldSelection, newSelection);
            shared.Inflate(-(BorderThickness + 2), -(BorderThickness + 2));
            if (shared.Width > 96 && shared.Height > 96) dirty.Exclude(shared);
        }

        // The badge can sit inside the selection when there is no room above it, so add
        // it back after the exclusion.
        if (!oldSelection.IsEmpty) dirty.Union(Pad(BadgeBounds(oldSelection)));
        if (!newSelection.IsEmpty) dirty.Union(Pad(BadgeBounds(newSelection)));

        Invalidate(dirty);

        static Rectangle Inflate(Rectangle r)
        {
            if (r.IsEmpty) return Rectangle.Empty;
            r.Inflate(BorderThickness + InvalidatePadding, BorderThickness + InvalidatePadding);
            return r;
        }
    }

    // ------------------------------------------------------------------ misc

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _measure?.Dispose();
            _dimmed.Dispose();
            _badgeFont.Dispose();
            _magFont.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
